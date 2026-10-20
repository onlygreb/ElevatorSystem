using System.Threading.Channels;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;
using ElevatorSystem.Domain.Scheduling;
using Microsoft.Extensions.Logging;
using Log = ElevatorSystem.Application.ElevatorControllerLog;

namespace ElevatorSystem.Application;

/// <summary>
/// Accepts passenger requests from any thread and drives one <see cref="Domain.Elevator"/> through
/// them in the order chosen by its scheduling strategy.
/// </summary>
/// <remarks>
/// <para>
/// Requests enter a bounded channel and are applied by a single control loop
/// (<see cref="ProcessRequests"/>), which also starts every trip and door cycle. While the car is
/// travelling the loop keeps applying new requests, so assigning a request never waits for the car.
/// </para>
/// <para>
/// Each trip is watched: one that does not arrive within <see cref="ElevatorControllerOptions.StuckTimeout"/>
/// takes the elevator out of service. Safety commands (<see cref="EmergencyStop"/>,
/// <see cref="StartMaintenance"/>) bypass the queue and take effect immediately.
/// See ADR-0004, ADR-0007 and ADR-0012.
/// </para>
/// <para>
/// A simulated building can be fast-forwarded (<see cref="SetSpeed"/>, ADR-0031): trips and door countdowns get
/// shorter, so the car behaves exactly the same, only sooner. The watchdog keeps its normal limit, so a host slowed
/// down by the load is never mistaken for a stuck car.
/// </para>
/// </remarks>
public sealed class ElevatorController
{
    /// <summary>The fastest a simulated building may run, relative to normal.</summary>
    public const double MaxSpeed = 10;

    private readonly Channel<Intake> _intake;
    private ISchedulingStrategy _strategy;
    private readonly IElevatorDrive _drive;
    private readonly IElevatorEventSink _eventSink;
    private readonly ElevatorControllerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ElevatorController> _logger;
    private readonly Lock _operationGate = new();
    private CancellationTokenSource? _currentOperation;
    private TaskCompletionSource _nudge = NewNudge();
    private DateTimeOffset? _doorsCloseAt;
    private bool _doorHeld;
    private double _speed = 1;
    private int _isProcessing;

    public ElevatorController(
        Elevator elevator,
        ISchedulingStrategy strategy,
        IElevatorDrive drive,
        IElevatorEventSink eventSink,
        ElevatorControllerOptions options,
        TimeProvider timeProvider,
        ILogger<ElevatorController> logger)
    {
        ArgumentNullException.ThrowIfNull(elevator);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentNullException.ThrowIfNull(eventSink);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();

        Elevator = elevator;
        _strategy = strategy;
        _drive = drive;
        _eventSink = eventSink;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _intake = Channel.CreateBounded<Intake>(new BoundedChannelOptions(options.IntakeCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public Elevator Elevator { get; }

    public ISchedulingStrategy Strategy => Volatile.Read(ref _strategy);

    /// <summary>The order the car serves its calls in now: LOOK when the strategy is LOOK, FIFO otherwise.</summary>
    public SchedulingPolicy Scheduling => Strategy is LookSchedulingStrategy ? SchedulingPolicy.Look : SchedulingPolicy.Fifo;

    /// <summary>Raised with the new policy after <see cref="ChangeScheduling"/> changes it. Handlers must be fast and thread-safe.</summary>
    public event EventHandler<SchedulingPolicy>? SchedulingChanged;

    public ElevatorControllerOptions Options => _options;

    /// <summary>How many times faster than normal the car runs: 1 unless a simulation fast-forwards it (ADR-0031).</summary>
    public double Speed => Volatile.Read(ref _speed);

    /// <summary>Raised with the new speed after <see cref="SetSpeed"/> changes it. Handlers must be fast and thread-safe.</summary>
    public event EventHandler<double>? SpeedChanged;

    /// <summary>
    /// When the open door will close, or <see langword="null"/> while it stays open because the car has nowhere to
    /// go (and while it is closed). The door stays open whenever nothing is pending; a new call starts the
    /// <see cref="ElevatorControllerOptions.DoorOpenDuration"/> countdown (ADR-0025).
    /// </summary>
    public DateTimeOffset? DoorsCloseAt
    {
        get
        {
            lock (_operationGate)
            {
                return _doorsCloseAt;
            }
        }
    }

    /// <summary>
    /// A pickup request from a landing: a passenger on <paramref name="floor"/> wants to go in
    /// <paramref name="direction"/>. Safe to call from any thread; never blocks.
    /// </summary>
    public RequestResult RequestElevator(int floor, Direction direction) =>
        Submit(new HallCall(new Floor(floor), direction));

    /// <summary>A destination request from inside the car. Safe to call from any thread; never blocks.</summary>
    public RequestResult RequestDestination(int floor) => Submit(new CarCall(new Floor(floor)));

    /// <summary>
    /// Submits a request created elsewhere, keeping its identifier and time: for example a command
    /// received from the message bus, whose id the API already returned to the caller.
    /// Safe to call from any thread; never blocks.
    /// </summary>
    public RequestResult Submit(PassengerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var call = request.Call;
        var rejection = Elevator.Building.Validate(call)
            ?? (Elevator.State == ElevatorState.OutOfService ? RequestRejectionReason.OutOfService : (RequestRejectionReason?)null);
        if (rejection is { } invalid)
        {
            Log.RequestRejected(_logger, call, invalid);
            return RequestResult.Rejected(invalid);
        }

        if (!_intake.Writer.TryWrite(new SubmittedRequest(request)))
        {
            Log.RequestRejected(_logger, call, RequestRejectionReason.Busy);
            return RequestResult.Rejected(RequestRejectionReason.Busy);
        }

        return RequestResult.Accepted(request.Id);
    }

    /// <summary>
    /// Fast-forwards a simulated car (ADR-0031): from the next trip or door countdown on, travel and the door countdown
    /// take their normal time divided by <paramref name="speed"/>; the watchdog keeps its limit. Safe to call from any thread.
    /// </summary>
    /// <returns><see langword="false"/> when the drive is real machinery, whose pace cannot change; then only 1 is accepted.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="speed"/> is below 1 or above <see cref="MaxSpeed"/>.</exception>
    public bool SetSpeed(double speed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(speed, 1.0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(speed, MaxSpeed);
        if (_drive is not IVariableSpeedDrive drive)
        {
            return speed == 1;
        }

        drive.SetSpeed(speed);
        if (Interlocked.Exchange(ref _speed, speed) != speed)
        {
            Log.SpeedChanged(_logger, speed);
            try
            {
                SpeedChanged?.Invoke(this, speed);
            }
            // A failing subscriber must never stop the elevator (ADR-0007).
            catch (Exception exception)
            {
                Log.EventSinkFailed(_logger, exception, nameof(SpeedChanged));
            }
        }

        return true;
    }

    /// <summary>
    /// Clears the queue (ADR-0032): every pending call except <paramref name="keep"/> is cancelled, then the car is sent to
    /// <paramref name="thenGoTo"/>. The order joins the requests already submitted, so those are cleared too, and anything
    /// submitted afterwards stays. Safe to call from any thread; never blocks.
    /// </summary>
    /// <returns><see langword="false"/> when the intake is full; nothing is cleared then.</returns>
    public bool ClearRequests(IReadOnlyCollection<ElevatorCall> keep, Floor? thenGoTo = null)
    {
        ArgumentNullException.ThrowIfNull(keep);
        if (thenGoTo is { } floor && !Elevator.Building.Contains(floor))
        {
            throw new ArgumentOutOfRangeException(nameof(thenGoTo), floor, "The floor is not in the building.");
        }

        return _intake.Writer.TryWrite(new ClearOrder(keep, thenGoTo));
    }

    /// <summary>
    /// Switches the scheduling strategy (ADR-0033). Only while the car is out of service: it then holds no calls, so no
    /// call is ever planned by two strategies. Lasts until the engine restarts; then the configured one applies.
    /// </summary>
    /// <returns><see langword="false"/> when the car is in service; nothing changes then.</returns>
    public bool ChangeScheduling(SchedulingPolicy policy)
    {
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "Choose Fifo or Look.");
        }

        if (Elevator.State != ElevatorState.OutOfService)
        {
            return false;
        }

        if (policy != Scheduling)
        {
            Volatile.Write(ref _strategy, (_options with { Scheduling = policy }).CreateStrategy());
            Log.SchedulingChanged(_logger, policy);
            try
            {
                SchedulingChanged?.Invoke(this, policy);
            }
            // A failing subscriber must never stop the elevator (ADR-0007).
            catch (Exception exception)
            {
                Log.EventSinkFailed(_logger, exception, nameof(SchedulingChanged));
            }
        }

        return true;
    }

    /// <summary>Stops the car at once and takes it out of service, cancelling every pending call.</summary>
    public TransitionResult EmergencyStop() => TakeOutOfService(OutOfServiceReason.EmergencyStop);

    /// <summary>Takes the car out of service for maintenance, cancelling every pending call.</summary>
    public TransitionResult StartMaintenance() => TakeOutOfService(OutOfServiceReason.Maintenance);

    /// <summary>Returns an out-of-service car to service; with nothing to do, it then opens its door and waits.</summary>
    public TransitionResult ReturnToService()
    {
        var result = Elevator.ReturnToService();
        Publish(result.Events);
        Nudge();
        return result;
    }

    /// <summary>
    /// Runs the control loop until <paramref name="cancellationToken"/> is cancelled: applies requests,
    /// moves the car floor by floor, cycles the door at each stop, and watches every trip.
    /// </summary>
    /// <exception cref="InvalidOperationException">The loop is already running: an elevator has exactly one.</exception>
    public async Task ProcessRequests(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _isProcessing, 1) == 1)
        {
            throw new InvalidOperationException("The control loop is already running; an elevator has exactly one.");
        }

        Log.ControlLoopStarted(_logger, Elevator.CurrentFloor, Strategy.Name);
        var events = new List<ElevatorEvent>();
        Operation? operation = null;
        Task<bool>? requestArrival = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    ApplyPendingRequests(events);
                    if (operation is { Completion.IsCompleted: true })
                    {
                        FinishOperation(operation, events);
                        operation = null;
                    }

                    operation ??= StartNextOperation(events, cancellationToken);
                }
                // Fail-safe: any unexpected failure must stop the car, not crash the loop.
                catch (Exception exception)
                {
                    Log.ControlLoopFailed(_logger, exception);
                    CancelCurrentOperation();
                    events.AddRange(Elevator.TakeOutOfService(OutOfServiceReason.Fault).Events);
                }

                // Publish only after the next operation has started, so an observer reacting to these
                // events always finds the controller in a consistent state.
                Publish(events);
                events.Clear();

                // Sleep until a request arrives, the current trip or door cycle ends, a nudge, or shutdown.
                requestArrival ??= _intake.Reader.WaitToReadAsync(cancellationToken).AsTask();
                var nudge = Volatile.Read(ref _nudge);
                Task wakeUp = operation is null
                    ? Task.WhenAny(requestArrival, nudge.Task)
                    : Task.WhenAny(requestArrival, operation.Completion, nudge.Task);
                await wakeUp.ConfigureAwait(false);
                if (nudge.Task.IsCompleted)
                {
                    Interlocked.CompareExchange(ref _nudge, NewNudge(), nudge);
                }

                if (requestArrival.IsCompleted)
                {
                    if (!requestArrival.IsCompletedSuccessfully || !await requestArrival.ConfigureAwait(false))
                    {
                        break;
                    }

                    requestArrival = null;
                }
            }
        }
        finally
        {
            CancelCurrentOperation();
            if (operation is not null)
            {
                EndOperation(operation.Cancellation);
            }

            Volatile.Write(ref _isProcessing, 0);
            Log.ControlLoopStopped(_logger);
        }
    }

    private RequestResult Submit(ElevatorCall call) =>
        Submit(new PassengerRequest(RequestId.New(_timeProvider), call, _timeProvider.GetUtcNow()));

    private TransitionResult TakeOutOfService(OutOfServiceReason reason)
    {
        // The elevator's own lock makes this take effect immediately, whatever the loop is doing.
        var result = Elevator.TakeOutOfService(reason);
        CancelCurrentOperation();
        Publish(result.Events);
        return result;
    }

    private void ApplyPendingRequests(List<ElevatorEvent> events)
    {
        while (_intake.Reader.TryRead(out var item))
        {
            switch (item)
            {
                case SubmittedRequest submitted:
                    try
                    {
                        events.AddRange(Elevator.AddRequest(submitted.Request).Events);
                    }
                    // One bad request must never stop the elevator; it is logged and dropped.
                    catch (Exception exception)
                    {
                        Log.RequestFailed(_logger, exception, submitted.Request.Id);
                    }

                    break;
                case ClearOrder clear:
                    events.AddRange(Elevator.ClearRequests(clear.Keep).Events);
                    if (clear.ThenGoTo is { } floor && Elevator.State != ElevatorState.OutOfService)
                    {
                        var request = new PassengerRequest(RequestId.New(_timeProvider), new CarCall(floor), _timeProvider.GetUtcNow());
                        events.AddRange(Elevator.AddRequest(request).Events);
                    }

                    break;
            }
        }
    }

    private Operation? StartNextOperation(List<ElevatorEvent> events, CancellationToken loopToken)
    {
        var snapshot = Elevator.Snapshot();
        switch (snapshot.State)
        {
            case ElevatorState.OutOfService:
                _doorHeld = false;
                SetDoorsCloseAt(null);
                return null;
            case ElevatorState.DoorOpen when _doorHeld:
                // Held open with nowhere to go: a call that gives the car somewhere to go starts the countdown.
                if (Strategy.SelectTarget(snapshot) is null)
                {
                    return null;
                }

                _doorHeld = false;
                return StartDoorCycle(loopToken);
            case ElevatorState.DoorOpen:
                return StartDoorCycle(loopToken);
        }

        var isMoving = snapshot.State is ElevatorState.MovingUp or ElevatorState.MovingDown;
        if (Strategy.SelectTarget(snapshot) is not { } target)
        {
            // Nothing left to serve: a moving car stops at the floor it is reaching; an idle car opens and waits.
            return isMoving ? OpenDoorHere(events, loopToken) : _options.HoldDoorOpenWhenIdle ? HoldDoorOpenHere(events) : null;
        }

        if (target == snapshot.CurrentFloor)
        {
            return OpenDoorHere(events, loopToken);
        }

        var direction = target > snapshot.CurrentFloor ? Direction.Up : Direction.Down;
        var mustReverse = snapshot.State == (direction == Direction.Up ? ElevatorState.MovingDown : ElevatorState.MovingUp);
        if (mustReverse)
        {
            // The car only changes direction after stopping.
            return OpenDoorHere(events, loopToken);
        }

        var move = direction == Direction.Up ? Elevator.MoveUp() : Elevator.MoveDown();
        if (!move.Succeeded)
        {
            Log.TransitionRefused(_logger, direction == Direction.Up ? nameof(Elevator.MoveUp) : nameof(Elevator.MoveDown), snapshot.CurrentFloor, move.Error!.Value);
            return null;
        }

        events.AddRange(move.Events);
        return StartTrip(direction, loopToken);
    }

    /// <summary>Opens the door at this floor with no countdown: the car has nowhere to go, so it waits open.</summary>
    private Operation? HoldDoorOpenHere(List<ElevatorEvent> events)
    {
        var opened = Elevator.OpenDoor();
        if (!opened.Succeeded)
        {
            Log.TransitionRefused(_logger, nameof(Elevator.OpenDoor), Elevator.CurrentFloor, opened.Error!.Value);
            return null;
        }

        events.AddRange(opened.Events);
        _doorHeld = true;
        SetDoorsCloseAt(null);
        return null;
    }

    private Operation? OpenDoorHere(List<ElevatorEvent> events, CancellationToken loopToken)
    {
        var opened = Elevator.OpenDoor();
        if (!opened.Succeeded)
        {
            Log.TransitionRefused(_logger, nameof(Elevator.OpenDoor), Elevator.CurrentFloor, opened.Error!.Value);
            return null;
        }

        events.AddRange(opened.Events);
        if (_options.HoldDoorOpenWhenIdle && Strategy.SelectTarget(Elevator.Snapshot()) is null)
        {
            // The stop served everything: no countdown, the car waits here with its door open.
            _doorHeld = true;
            SetDoorsCloseAt(null);
            return null;
        }

        return StartDoorCycle(loopToken);
    }

    private Operation StartTrip(Direction direction, CancellationToken loopToken)
    {
        SetDoorsCloseAt(null);
        var cancellation = BeginOperation(loopToken);
        Task trip;
        try
        {
            trip = _drive.MoveOneFloorAsync(direction, cancellation.Token);
        }
        // A drive that throws is a fault like any other: the watchdog path handles it.
        catch (Exception exception)
        {
            trip = Task.FromException(exception);
        }

        var watched = trip.WaitAsync(_options.StuckTimeout, _timeProvider, cancellation.Token);
        return new Operation(OperationKind.Trip, watched, cancellation);
    }

    private Operation StartDoorCycle(CancellationToken loopToken)
    {
        var cancellation = BeginOperation(loopToken);
        var duration = _options.DoorOpenDuration / Speed;
        SetDoorsCloseAt(_timeProvider.GetUtcNow() + duration);
        var dwell = Task.Delay(duration, _timeProvider, cancellation.Token);
        return new Operation(OperationKind.DoorCycle, dwell, cancellation);
    }

    private void FinishOperation(Operation operation, List<ElevatorEvent> events)
    {
        EndOperation(operation.Cancellation);
        var completion = operation.Completion;
        if (completion.IsCanceled)
        {
            // Stopped by a safety command or shutdown; the elevator's state already reflects it.
            return;
        }

        if (completion.Exception is { } failure)
        {
            var cause = failure.GetBaseException();
            if (cause is TimeoutException)
            {
                Log.ElevatorStuck(_logger, Elevator.CurrentFloor, _options.StuckTimeout);
            }
            else
            {
                Log.DriveFailed(_logger, cause, Elevator.CurrentFloor);
            }

            events.AddRange(Elevator.TakeOutOfService(OutOfServiceReason.Fault).Events);
            return;
        }

        if (operation.Kind == OperationKind.DoorCycle)
        {
            SetDoorsCloseAt(null);
            if (_options.HoldDoorOpenWhenIdle && Strategy.SelectTarget(Elevator.Snapshot()) is null)
            {
                // Nowhere to go: the door stays open until a call arrives (ADR-0025).
                _doorHeld = true;
                return;
            }

            events.AddRange(Elevator.CloseDoor().Events);
        }

        // A completed trip needs nothing here: the next decision stops at this floor or continues.
    }

    private CancellationTokenSource BeginOperation(CancellationToken loopToken)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(loopToken);
        lock (_operationGate)
        {
            _currentOperation = cancellation;
        }

        // A safety command may have landed between the decision and this point; honour it.
        if (Elevator.State == ElevatorState.OutOfService)
        {
            cancellation.Cancel();
        }

        return cancellation;
    }

    private void EndOperation(CancellationTokenSource cancellation)
    {
        lock (_operationGate)
        {
            if (ReferenceEquals(_currentOperation, cancellation))
            {
                _currentOperation = null;
            }
        }

        cancellation.Dispose();
    }

    private void CancelCurrentOperation()
    {
        CancellationTokenSource? current;
        lock (_operationGate)
        {
            current = _currentOperation;
        }

        try
        {
            current?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The operation finished at the same moment; there is nothing left to cancel.
        }
    }

    private static TaskCompletionSource NewNudge() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Wakes the control loop to look at the elevator again, for example after a return to service.</summary>
    private void Nudge() => Volatile.Read(ref _nudge).TrySetResult();

    private void SetDoorsCloseAt(DateTimeOffset? closesAt)
    {
        lock (_operationGate)
        {
            _doorsCloseAt = closesAt;
        }
    }

    private void Publish(IReadOnlyList<ElevatorEvent> events)
    {
        foreach (var elevatorEvent in events)
        {
            LogEvent(elevatorEvent);
            try
            {
                _eventSink.Publish(elevatorEvent);
            }
            // A failing subscriber must never stop the elevator (ADR-0007).
            catch (Exception exception)
            {
                Log.EventSinkFailed(_logger, exception, elevatorEvent.GetType().Name);
            }
        }
    }

    private void LogEvent(ElevatorEvent elevatorEvent)
    {
        switch (elevatorEvent)
        {
            case CallQueued queued:
                Log.CallQueued(_logger, queued.Request.Call, queued.Request.Id, queued.Position);
                break;
            case CallMerged merged:
                Log.CallMerged(_logger, merged.Request.Id, merged.Request.Call, merged.Position);
                break;
            case RequestRejected rejected:
                Log.RequestRejected(_logger, rejected.Request.Call, rejected.Reason);
                break;
            case ElevatorMoved moved:
                Log.ElevatorMoved(_logger, moved.Direction, moved.From, moved.Floor);
                break;
            case DoorsOpened opened:
                Log.DoorsOpened(_logger, opened.Floor);
                break;
            case CallServed served:
                Log.CallServed(_logger, served.Call.Call, served.Call.RequestCount, served.WaitTime);
                break;
            case DoorsClosed closed:
                Log.DoorsClosed(_logger, closed.Floor);
                break;
            case ElevatorTakenOutOfService outOfService:
                Log.TakenOutOfService(_logger, outOfService.Floor, outOfService.Reason, outOfService.CancelledCalls.Length);
                break;
            case RequestsCleared cleared:
                Log.RequestsCleared(_logger, cleared.CancelledCalls.Length);
                break;
            case ElevatorReturnedToService returned:
                Log.ReturnedToService(_logger, returned.Floor);
                break;
        }
    }

    /// <summary>What the control loop takes in, in order: requests, and orders to clear the queue.</summary>
    private abstract record Intake;

    private sealed record SubmittedRequest(PassengerRequest Request) : Intake;

    private sealed record ClearOrder(IReadOnlyCollection<ElevatorCall> Keep, Floor? ThenGoTo) : Intake;

    private enum OperationKind
    {
        Trip,
        DoorCycle,
    }

    private sealed record Operation(OperationKind Kind, Task Completion, CancellationTokenSource Cancellation);
}
