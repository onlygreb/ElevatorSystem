using ElevatorSystem.Application.Diagnostics;
using ElevatorSystem.Application.Passengers;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Application.Simulation;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;
using ElevatorSystem.Domain.Scheduling;
using Microsoft.Extensions.Logging;
using Log = ElevatorSystem.Application.Hosting.ElevatorEngineLog;

namespace ElevatorSystem.Application.Hosting;

/// <summary>What an engine instance is doing.</summary>
public enum EngineRole
{
    /// <summary>Not yet competing for leadership (for example, still migrating the database).</summary>
    Starting,

    /// <summary>Waiting for leadership; another instance may be driving the car.</summary>
    Standby,

    /// <summary>Driving the car and consuming commands.</summary>
    Leader,

    /// <summary>Shut down.</summary>
    Stopped,
}

/// <summary>
/// Runs the elevator behind the messaging and persistence ports with one fail-safe cycle (ADR-0007, ADR-0021):
/// acquire leadership, restore the last snapshot (or start fresh), drive the car while consuming commands,
/// and the moment leadership is lost stop driving and go back to acquiring.
/// </summary>
/// <remarks>
/// <para>
/// Each term of leadership builds its own <see cref="ElevatorController"/> from the stored snapshot, so a
/// standby never holds state that could drift from what the leader saved. The controller's events go to an
/// <see cref="PassengerRegistry"/>, a <see cref="SnapshotPersister"/> and an <see cref="ElevatorUpdateForwarder"/>, which publishes
/// an elevator update per event (and per passenger step) and an occupancy update per change of the counts, and to the
/// <see cref="ElevatorMetrics"/> when the host measures; the control loop never waits for any of them. The term also
/// runs the demo scenarios (ADR-0027).
/// </para>
/// <para>
/// A term opens with a start-up door cycle at the floor where the car stands: the engine cannot know
/// whether anyone is inside, the restored state reaches every subscriber with a fresh version, and calls
/// for that floor are served. An out-of-service car stays as it is.
/// </para>
/// <para>
/// A graceful stop ends consumption first, so every acknowledged command has reached the car, then stops
/// the car at its floor with the door open, saves that state and publishes the last updates within
/// <see cref="ElevatorEngineOptions.ShutdownTimeout"/>, and only then releases leadership. A lost lease
/// skips all of that: another engine may already be taking over.
/// </para>
/// </remarks>
public sealed class ElevatorEngine
{
    private readonly ILeaderElector _leaderElector;
    private readonly IElevatorStateStore _stateStore;
    private readonly IProcessedCommandStore _processedCommands;
    private readonly IElevatorCommandConsumer _commandConsumer;
    private readonly IElevatorUpdatePublisher _updatePublisher;
    private readonly ISchedulingStrategy _strategy;
    private readonly Func<IElevatorDrive> _createDrive;
    private readonly PassengerOptions _passengerOptions;
    private readonly SimulationOptions _simulationOptions;
    private readonly ElevatorEngineOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly ElevatorMetrics? _metrics;
    private volatile ElevatorController? _controller;
    private volatile PassengerRegistry? _passengers;
    private volatile DemoScenarioRunner? _scenarios;
    private volatile int _role = (int)EngineRole.Starting;
    private int _isRunning;

    public ElevatorEngine(
        ILeaderElector leaderElector,
        IElevatorStateStore stateStore,
        IProcessedCommandStore processedCommands,
        IElevatorCommandConsumer commandConsumer,
        IElevatorUpdatePublisher updatePublisher,
        Building building,
        ISchedulingStrategy strategy,
        Func<IElevatorDrive> createDrive,
        ElevatorControllerOptions controllerOptions,
        ElevatorEngineOptions options,
        PassengerOptions passengerOptions,
        SimulationOptions simulationOptions,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        ElevatorMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(leaderElector);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(processedCommands);
        ArgumentNullException.ThrowIfNull(commandConsumer);
        ArgumentNullException.ThrowIfNull(updatePublisher);
        ArgumentNullException.ThrowIfNull(building);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(createDrive);
        ArgumentNullException.ThrowIfNull(controllerOptions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(passengerOptions);
        ArgumentNullException.ThrowIfNull(simulationOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        controllerOptions.Validate();
        options.Validate();
        passengerOptions.Validate();
        simulationOptions.Validate();

        _leaderElector = leaderElector;
        _stateStore = stateStore;
        _processedCommands = processedCommands;
        _commandConsumer = commandConsumer;
        _updatePublisher = updatePublisher;
        Building = building;
        _strategy = strategy;
        _createDrive = createDrive;
        ControllerOptions = controllerOptions;
        _options = options;
        _passengerOptions = passengerOptions;
        _simulationOptions = simulationOptions;
        _timeProvider = timeProvider;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ElevatorEngine>();
        _metrics = metrics;
    }

    /// <summary>What this instance is doing right now.</summary>
    public EngineRole Role => (EngineRole)_role;

    /// <summary>The controller driving the car while this instance leads; otherwise <see langword="null"/>.</summary>
    public ElevatorController? Controller => _controller;

    /// <summary>The passengers while this instance leads; otherwise <see langword="null"/>.</summary>
    public PassengerRegistry? Passengers => _passengers;

    /// <summary>The demo scenarios while this instance leads; otherwise <see langword="null"/>.</summary>
    public DemoScenarioRunner? Scenarios => _scenarios;

    public Building Building { get; }

    public ElevatorControllerOptions ControllerOptions { get; }

    /// <summary>
    /// Competes for leadership and drives the car whenever it wins, until <paramref name="stoppingToken"/> is
    /// cancelled. Returns normally on shutdown. An unexpected end of the control loop or of the consumer is
    /// rethrown, so the host stops and the orchestrator restarts it (ADR-0007).
    /// </summary>
    /// <exception cref="InvalidOperationException">The engine is already running.</exception>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (Interlocked.Exchange(ref _isRunning, 1) == 1)
        {
            throw new InvalidOperationException("The engine is already running.");
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                SetRole(EngineRole.Standby);
                Log.StandingBy(_logger);
                ILeadershipLease lease;
                try
                {
                    lease = await _leaderElector.AcquireAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await LeadAsync(lease, stoppingToken);
                }
                finally
                {
                    await ReleaseAsync(lease);
                }
            }
        }
        finally
        {
            SetRole(EngineRole.Stopped);
            Volatile.Write(ref _isRunning, 0);
        }
    }

    private async Task LeadAsync(ILeadershipLease lease, CancellationToken stoppingToken)
    {
        using var term = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.Lost);
        var stored = await LoadSnapshotAsync(term.Token);
        if (term.IsCancellationRequested)
        {
            if (IsLost(lease, stoppingToken))
            {
                Log.LeadershipLost(_logger);
            }

            return;
        }

        var elevator = Restore(stored);
        var persister = new SnapshotPersister(elevator, _stateStore, stored?.Version ?? -1, _options, _timeProvider, _logger);
        var forwarder = new ElevatorUpdateForwarder(_updatePublisher, persister, _options, _timeProvider, _logger);
        var sink = new EngineEventSink(elevator, persister, forwarder, _timeProvider);
        // Passengers live in this term's memory (ADR-0026): after a failover they join again. It sees every event first.
        var passengers = new PassengerRegistry(
            elevator, _passengerOptions, _timeProvider, _loggerFactory.CreateLogger<PassengerRegistry>(), new PassengerForwarding(forwarder, sink, _timeProvider, _metrics));
        IElevatorEventSink controllerSink = new CompositeElevatorEventSink(
            _metrics is null ? [passengers, sink] : [passengers, sink, _metrics], _loggerFactory.CreateLogger<CompositeElevatorEventSink>());
        var controller = new ElevatorController(
            elevator, _strategy, _createDrive(), controllerSink, ControllerOptions, _timeProvider, _loggerFactory.CreateLogger<ElevatorController>());
        sink.Controller = controller;
        controller.SpeedChanged += (_, speed) => sink.PublishSpeedChanged(speed);
        controller.SchedulingChanged += (_, policy) => sink.PublishSchedulingChanged(policy);
        using var scenarios = new DemoScenarioRunner(
            new PassengerService(passengers, controller, _timeProvider), elevator, _simulationOptions, _timeProvider, _loggerFactory.CreateLogger<DemoScenarioRunner>());

        // Not linked to the term: the order in which these stop matters, so each is cancelled explicitly.
        using var consuming = new CancellationTokenSource();
        using var driving = new CancellationTokenSource();
        using var workers = new CancellationTokenSource();
        var handler = new EngineCommandHandler(controller, passengers, scenarios, _processedCommands, sink, _options, _timeProvider, _logger, driving.Token);
        var persisting = persister.RunAsync(workers.Token);
        var forwarding = forwarder.RunAsync(workers.Token);
        persister.Notify();
        forwarder.Enqueue(new OccupancyUpdateV1(Guid.CreateVersion7(_timeProvider.GetUtcNow()), passengers.Current));

        OpenDoorOnTakeOver(elevator, controllerSink);
        _controller = controller;
        _passengers = passengers;
        _scenarios = scenarios;
        SetRole(EngineRole.Leader);
        Log.BecameLeader(_logger);

        var loop = controller.ProcessRequests(driving.Token);
        var consumer = _commandConsumer.ConsumeAsync(handler.HandleAsync, consuming.Token);
        try
        {
            var ended = await Task.WhenAny(loop, consumer, Task.Delay(Timeout.InfiniteTimeSpan, _timeProvider, term.Token));
            if (ended == loop || ended == consumer)
            {
                // Both run until cancelled; ending on their own is a fault the host must not hide.
                await ended;
                throw new InvalidOperationException(ended == loop
                    ? "The control loop ended while this engine was leading."
                    : "Command consumption ended while this engine was leading.");
            }
        }
        finally
        {
            _controller = null;
            _passengers = null;
            _scenarios = null;
            scenarios.Dispose();
            if (IsLost(lease, stoppingToken))
            {
                // Another engine may take over within moments: stop driving, consuming and saving at once.
                await driving.CancelAsync();
                await consuming.CancelAsync();
                await workers.CancelAsync();
                await Settle(loop, consumer, persisting, forwarding);
                Log.LeadershipLost(_logger);
            }
            else
            {
                // Consumption ends first, so a command being handled still reaches the car; then the car stops.
                await consuming.CancelAsync();
                await Settle(consumer);
                await driving.CancelAsync();
                await Settle(loop);
                await StopGracefullyAsync(elevator, controllerSink, persister, forwarder, forwarding, lease.Lost);
                await workers.CancelAsync();
                await Settle(persisting, forwarding);
                Log.StoppedLeading(_logger, elevator.CurrentFloor);
            }

            // Last, after the parking events: a standby reports no floor.
            _metrics?.Forget();
        }
    }

    private async Task<ElevatorSnapshot?> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var timeout = new CancellationTokenSource(_options.StoreTimeout, _timeProvider);
                using var load = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                return await _stateStore.LoadAsync(load.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            // Without the last state the car must not move: it may hold calls that nobody would serve.
            catch (Exception exception)
            {
                Log.LoadFailed(_logger, exception, _options.StoreRetryInterval);
            }

            try
            {
                await Task.Delay(_options.StoreRetryInterval, _timeProvider, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        return null;
    }

    private Elevator Restore(ElevatorSnapshot? stored)
    {
        if (stored is null)
        {
            var fresh = new Elevator(Building, _timeProvider);
            Log.StartingFresh(_logger, fresh.CurrentFloor);
            return fresh;
        }

        try
        {
            var restored = Elevator.Restore(Building, _timeProvider, stored);
            var snapshot = restored.Snapshot();
            Log.Restored(_logger, snapshot.CurrentFloor, snapshot.State, snapshot.PendingCalls.Length, snapshot.Version);
            return restored;
        }
        // A snapshot this building cannot hold (a floor that no longer exists) must not stop the service.
        catch (ArgumentException exception)
        {
            Log.RestoreFailed(_logger, exception);

            // Keep counting from the stored version, or every save of the new state would be ignored as stale.
            var empty = new ElevatorSnapshot(Building.LowestFloor, ElevatorState.Idle, null, null, [], stored.Version, _timeProvider.GetUtcNow());
            return Elevator.Restore(Building, _timeProvider, empty);
        }
    }

    private void OpenDoorOnTakeOver(Elevator elevator, IElevatorEventSink sink)
    {
        if (elevator.State == ElevatorState.OutOfService)
        {
            return;
        }

        var opened = elevator.OpenDoor();
        if (opened.Events.IsEmpty)
        {
            return;
        }

        Log.StartUpDoorCycle(_logger, elevator.CurrentFloor);
        Publish(sink, opened.Events);
    }

    private async Task StopGracefullyAsync(
        Elevator elevator,
        IElevatorEventSink sink,
        SnapshotPersister persister,
        ElevatorUpdateForwarder forwarder,
        Task forwarding,
        CancellationToken leaseLost)
    {
        // A trip in progress ends at the floor it was heading to, with the door open (ADR-0007 §8).
        if (elevator.State is ElevatorState.MovingUp or ElevatorState.MovingDown)
        {
            Publish(sink, elevator.OpenDoor().Events);
            Log.Parked(_logger, elevator.CurrentFloor);
        }

        using var timeout = new CancellationTokenSource(_options.ShutdownTimeout, _timeProvider);
        using var flush = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, leaseLost);
        try
        {
            await persister.FlushAsync(flush.Token);
            forwarder.Complete();
            await forwarding.WaitAsync(flush.Token);
        }
        catch (OperationCanceledException)
        {
            Log.ShutdownFlushIncomplete(_logger, _options.ShutdownTimeout);
        }
    }

    private async Task ReleaseAsync(ILeadershipLease lease)
    {
        try
        {
            await lease.DisposeAsync();
        }
        // The database is probably unreachable; the server frees the lock when the session ends (ADR-0019).
        catch (Exception exception)
        {
            Log.ReleaseFailed(_logger, exception);
        }
    }

    private static void Publish(IElevatorEventSink sink, IEnumerable<ElevatorEvent> events)
    {
        foreach (var elevatorEvent in events)
        {
            sink.Publish(elevatorEvent);
        }
    }

    private static bool IsLost(ILeadershipLease lease, CancellationToken stoppingToken) =>
        lease.Lost.IsCancellationRequested && !stoppingToken.IsCancellationRequested;

    /// <summary>Waits for work that was told to stop; how it ended no longer matters.</summary>
    private static async Task Settle(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception)
        {
            // Stopping: a failure here was reported where it happened, or is the cancellation itself.
        }
    }

    private void SetRole(EngineRole role) => _role = (int)role;
}

/// <summary>
/// Turns every passenger step into an <see cref="ElevatorUpdateV1"/> (with the status right after it) and every change of the
/// counts into an <see cref="OccupancyUpdateV1"/>, for the forwarder, and tells the metrics; never blocks.
/// </summary>
internal sealed class PassengerForwarding(ElevatorUpdateForwarder forwarder, EngineEventSink sink, TimeProvider timeProvider, ElevatorMetrics? metrics)
    : IPassengerObserver
{
    public void PassengerChanged(PassengerEvent passengerEvent)
    {
        metrics?.PassengerChanged(passengerEvent);
        forwarder.Enqueue(new ElevatorUpdateV1(Guid.CreateVersion7(timeProvider.GetUtcNow()), PassengerActivity.ToActivity(passengerEvent), sink.CurrentStatus()));
    }

    public void OccupancyChanged(BuildingOccupancyDto occupancy)
    {
        metrics?.OccupancyChanged(occupancy);
        forwarder.Enqueue(new OccupancyUpdateV1(Guid.CreateVersion7(timeProvider.GetUtcNow()), occupancy));
    }
}
