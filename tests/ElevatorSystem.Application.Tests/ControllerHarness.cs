using ElevatorSystem.Application.Ports;
using ElevatorSystem.Application.Simulation;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;
using ElevatorSystem.Domain.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ElevatorSystem.Application.Tests;

/// <summary>
/// Runs an <see cref="ElevatorController"/> in simulated time. Every trip and every door cycle takes
/// exactly one <see cref="Step"/>, so advancing one step always completes the operation in progress
/// and produces at least one event. Tests move the world forward step by step, with no real waiting.
/// </summary>
internal sealed class ControllerHarness : IAsyncDisposable
{
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(1);
    public static readonly DateTimeOffset StartTime = new(2026, 1, 5, 8, 0, 0, TimeSpan.Zero);

    public static readonly ElevatorControllerOptions DefaultOptions = new()
    {
        TravelTimePerFloor = Step,
        DoorOpenDuration = Step,
        StuckTolerance = 3,
        IntakeCapacity = 64,
    };

    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;
    private int _cursor;

    private ControllerHarness(ElevatorController controller, FakeTimeProvider time, EventProbe probe, SimulatedElevatorDrive? simulatedDrive)
    {
        Controller = controller;
        Time = time;
        Probe = probe;
        SimulatedDrive = simulatedDrive;
    }

    public ElevatorController Controller { get; }

    public Elevator Elevator => Controller.Elevator;

    public FakeTimeProvider Time { get; }

    public EventProbe Probe { get; }

    /// <summary>The drive in use when the test did not supply its own.</summary>
    public SimulatedElevatorDrive? SimulatedDrive { get; }

    public Task Loop => _loop ?? throw new InvalidOperationException("The control loop has not been started.");

    public static ControllerHarness Create(
        int startFloor = 1,
        Func<TimeProvider, IElevatorDrive>? drive = null,
        ISchedulingStrategy? strategy = null,
        ElevatorControllerOptions? options = null,
        Func<EventProbe, IElevatorEventSink>? sink = null)
    {
        var time = new FakeTimeProvider(StartTime);
        var probe = new EventProbe();
        var simulatedDrive = drive is null ? new SimulatedElevatorDrive(Step, time) : null;
        var controller = new ElevatorController(
            new Elevator(Building.Default, time, new Floor(startFloor)),
            strategy ?? new FifoSchedulingStrategy(),
            simulatedDrive ?? drive!(time),
            sink?.Invoke(probe) ?? probe,
            options ?? DefaultOptions,
            time,
            NullLogger<ElevatorController>.Instance);
        return new ControllerHarness(controller, time, probe, simulatedDrive);
    }

    public ControllerHarness Start()
    {
        _loop = Controller.ProcessRequests(_shutdown.Token);
        return this;
    }

    /// <summary>Submits a hall call and waits until the control loop has taken it in.</summary>
    public Task<RequestResult> CallElevatorAsync(int floor, Direction direction) =>
        TakenInAsync(Controller.RequestElevator(floor, direction));

    /// <summary>Submits a car call and waits until the control loop has taken it in.</summary>
    public Task<RequestResult> SelectFloorAsync(int floor) => TakenInAsync(Controller.RequestDestination(floor));

    /// <summary>The next event of type <typeparamref name="T"/> after the last one consumed, without advancing time.</summary>
    public async Task<T> NextAsync<T>(Func<T, bool>? match = null)
        where T : ElevatorEvent
    {
        var (found, index) = await Probe.WaitForAsync(match, _cursor);
        _cursor = index + 1;
        return found;
    }

    /// <summary>Advances simulated time one step at a time until an event of type <typeparamref name="T"/> appears.</summary>
    public async Task<T> AdvanceUntilAsync<T>(Func<T, bool>? match = null, int maxSteps = 100)
        where T : ElevatorEvent
    {
        for (var step = 0; ; step++)
        {
            if (Probe.TryFind(match, _cursor, out var found, out var index))
            {
                _cursor = index + 1;
                return found;
            }

            if (step == maxSteps)
            {
                throw new TimeoutException($"No {typeof(T).Name} after {maxSteps} steps. Events:{Environment.NewLine}{Probe.Describe()}");
            }

            await StepAsync($"a {typeof(T).Name}");
        }
    }

    /// <summary>Advances until the car is idle with nothing pending; returns the events published meanwhile.</summary>
    public async Task<IReadOnlyList<ElevatorEvent>> RunUntilIdleAsync(int maxSteps = 200)
    {
        var from = _cursor;
        for (var step = 0; step < maxSteps; step++)
        {
            var snapshot = Elevator.Snapshot();
            if (snapshot.State == ElevatorState.Idle && snapshot.PendingCalls.IsEmpty)
            {
                var events = Probe.Events;
                _cursor = events.Count;
                return [.. events.Skip(from)];
            }

            await StepAsync("the car to become idle");
        }

        throw new TimeoutException($"The car was not idle after {maxSteps} steps. Events:{Environment.NewLine}{Probe.Describe()}");
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        if (_loop is not null)
        {
            await _loop;
        }

        _shutdown.Dispose();
    }

    private async Task StepAsync(string goal)
    {
        var snapshot = Elevator.Snapshot();
        if (snapshot.State == ElevatorState.OutOfService
            || (snapshot.State == ElevatorState.Idle && snapshot.PendingCalls.IsEmpty))
        {
            throw new InvalidOperationException(
                $"Waiting for {goal}, but the car is {snapshot.State} with nothing to do. Events:{Environment.NewLine}{Probe.Describe()}");
        }

        var published = Probe.Count;
        Time.Advance(Step);
        await Probe.WaitForCountAsync(published + 1);
    }

    private async Task<RequestResult> TakenInAsync(RequestResult result)
    {
        if (result.RequestId is { } id)
        {
            await Probe.WaitForAsync<ElevatorEvent>(e => e switch
            {
                CallQueued queued => queued.Request.Id == id,
                CallMerged merged => merged.Request.Id == id,
                RequestRejected rejected => rejected.Request.Id == id,
                CallServed served => served.Call.FirstRequestId == id,
                _ => false,
            });
        }

        return result;
    }
}
