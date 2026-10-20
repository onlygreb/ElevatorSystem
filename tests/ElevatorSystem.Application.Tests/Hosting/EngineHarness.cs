using ElevatorSystem.Application.Hosting;
using ElevatorSystem.Application.InMemory.Messaging;
using ElevatorSystem.Application.InMemory.Persistence;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Application.Simulation;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Scheduling;
using Microsoft.Extensions.Logging;

namespace ElevatorSystem.Application.Tests.Hosting;

/// <summary>
/// One or more engines on the in-memory bus, stores and leader elector, in real time with short timings.
/// Every update the engines publish is recorded, in order, by a subscriber that starts before them.
/// </summary>
internal sealed class EngineHarness : IAsyncDisposable
{
    public static readonly TimeSpan TravelTime = TimeSpan.FromMilliseconds(20);
    public static readonly TimeSpan DoorTime = TimeSpan.FromMilliseconds(20);

    private readonly CancellationTokenSource _subscription = new();
    private readonly List<RunningEngine> _engines = [];
    private readonly Task _subscribing;

    public EngineHarness()
    {
        Bus = new InMemoryMessageBus(Logs.CreateLogger<InMemoryMessageBus>());
        _subscribing = Bus.SubscribeAsync(
            (update, _) =>
            {
                Updates.Add(update);
                return Task.CompletedTask;
            },
            (update, _) =>
            {
                OccupancyUpdates.Add(update);
                return Task.CompletedTask;
            },
            _subscription.Token);
    }

    public RecordingLoggerFactory Logs { get; } = new();

    public InMemoryMessageBus Bus { get; }

    public IElevatorStateStore StateStore { get; set; } = new InMemoryElevatorStateStore();

    public IProcessedCommandStore ProcessedCommands { get; set; } = new InMemoryProcessedCommandStore(TimeProvider.System);

    public IElevatorUpdatePublisher? UpdatePublisher { get; set; }

    public InMemoryLeaderElector Elector { get; } = new();

    public ElevatorEngineOptions Options { get; set; } = new()
    {
        StatusRefreshInterval = TimeSpan.FromSeconds(30),
        StoreTimeout = TimeSpan.FromSeconds(5),
        StoreRetryInterval = TimeSpan.FromMilliseconds(50),
        ShutdownTimeout = TimeSpan.FromSeconds(5),
    };

    public ElevatorControllerOptions ControllerOptions { get; set; } = new()
    {
        TravelTimePerFloor = TravelTime,
        DoorOpenDuration = DoorTime,
        // These suites follow the classic cycle (the door closes after every stop); the default is covered elsewhere.
        HoldDoorOpenWhenIdle = false,
        // Generous, so a test host starved of CPU is never mistaken for a stuck car.
        StuckTolerance = 100,
    };

    public MessageRecorder<ElevatorUpdateV1> Updates { get; } = new();

    public MessageRecorder<OccupancyUpdateV1> OccupancyUpdates { get; } = new();

    /// <summary>Starts an engine that competes with the others for leadership.</summary>
    public RunningEngine Start()
    {
        var engine = new ElevatorEngine(
            Elector,
            StateStore,
            ProcessedCommands,
            Bus,
            UpdatePublisher ?? Bus,
            Building.Default,
            new FifoSchedulingStrategy(),
            () => new SimulatedElevatorDrive(ControllerOptions.TravelTimePerFloor, TimeProvider.System),
            ControllerOptions,
            Options,
            new ElevatorSystem.Application.Occupancy.OccupancyOptions(),
            TimeProvider.System,
            Logs);
        var running = new RunningEngine(engine);
        _engines.Add(running);
        return running;
    }

    /// <summary>Starts an engine and waits until it leads and has announced itself with its start-up door cycle.</summary>
    public async Task<RunningEngine> StartLeaderAsync()
    {
        var from = Updates.Count;
        var running = Start();
        await running.WaitForRoleAsync(EngineRole.Leader);
        await Updates.WaitForAsync(update => update.Activity.Kind == ElevatorActivityKind.DoorsOpened, from);
        return running;
    }

    public Task SendAsync(ElevatorCommandV1 command) => Bus.PublishAsync(command, TestContext.Current.CancellationToken);

    public static HallCallCommandV1 HallCall(int floor, CallDirection direction) =>
        new(Guid.CreateVersion7(), DateTimeOffset.UnixEpoch.AddYears(56), floor, direction);

    public static CarCallCommandV1 CarCall(int floor) => new(Guid.CreateVersion7(), DateTimeOffset.UnixEpoch.AddYears(56), floor);

    public static ServiceCommandV1 Service(ServiceAction action) => new(Guid.CreateVersion7(), DateTimeOffset.UnixEpoch.AddYears(56), action);

    public async ValueTask DisposeAsync()
    {
        foreach (var engine in _engines)
        {
            await engine.DisposeAsync();
        }

        await _subscription.CancelAsync();
        await _subscribing;
        _subscription.Dispose();
        Elector.Dispose();
    }
}

/// <summary>An engine and the task that runs it.</summary>
internal sealed class RunningEngine : IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();

    public RunningEngine(ElevatorEngine engine)
    {
        Engine = engine;
        Running = Task.Run(() => engine.RunAsync(_stopping.Token));
    }

    public ElevatorEngine Engine { get; }

    public Task Running { get; }

    public async Task WaitForRoleAsync(EngineRole role)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (Engine.Role != role)
        {
            if (guard.IsCancellationRequested)
            {
                throw new TimeoutException($"The engine never became {role}; it is {Engine.Role}.");
            }

            if (Running.IsFaulted)
            {
                await Running;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), CancellationToken.None);
        }
    }

    /// <summary>Stops the engine as the host does at shutdown, and waits for it.</summary>
    public async Task StopAsync()
    {
        if (!_stopping.IsCancellationRequested)
        {
            await _stopping.CancelAsync();
        }

        await Running.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stopping.Dispose();
    }
}
