using ElevatorSystem.Application.Occupancy;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Application.Simulation;
using ElevatorSystem.Application.Tests.Occupancy;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace ElevatorSystem.Application.Tests.Simulation;

/// <summary>
/// A controller in simulated time with an <see cref="OccupancyTracker"/>, a <see cref="TrafficGenerator"/>
/// and a <see cref="OccupancyConsistencyCheck"/> behind it, wired as a host would: one composite sink,
/// the tracker first. <see cref="StepAsync()"/> moves time one step and waits until the loop has taken
/// in every press the generator made, so runs never wait in real time.
/// </summary>
internal sealed class TrafficSimulation : IAsyncDisposable
{
    private TrafficSimulation(
        ControllerHarness harness,
        OccupancyTracker tracker,
        TrafficGenerator generator,
        OccupancyConsistencyCheck check,
        RecordingLogger<OccupancyTracker> trackerLog,
        RecordingLogger<TrafficGenerator> generatorLog)
    {
        Harness = harness;
        Tracker = tracker;
        Generator = generator;
        Check = check;
        TrackerLog = trackerLog;
        GeneratorLog = generatorLog;
    }

    public ControllerHarness Harness { get; }

    public OccupancyTracker Tracker { get; }

    public TrafficGenerator Generator { get; }

    public OccupancyConsistencyCheck Check { get; }

    public RecordingLogger<OccupancyTracker> TrackerLog { get; }

    public RecordingLogger<TrafficGenerator> GeneratorLog { get; }

    public EventProbe Probe => Harness.Probe;

    public Elevator Elevator => Harness.Elevator;

    /// <summary>Creates the simulation; the control loop runs only when <paramref name="startLoop"/> is set.</summary>
    public static TrafficSimulation Create(
        TrafficGeneratorOptions options,
        int capacity = 8,
        bool startLoop = true,
        int intakeCapacity = 64)
    {
        var lateBound = new LateBoundSink();
        var harness = ControllerHarness.Create(
            options: ControllerHarness.DefaultOptions with { IntakeCapacity = intakeCapacity },
            sink: probe => new CompositeElevatorEventSink([lateBound, probe], NullLogger<CompositeElevatorEventSink>.Instance));

        var occupancy = new OccupancyOptions { CarCapacity = capacity };
        var trackerLog = new RecordingLogger<OccupancyTracker>();
        var generatorLog = new RecordingLogger<TrafficGenerator>();
        var tracker = new OccupancyTracker(Building.Default, occupancy, harness.Time, trackerLog);
        var generator = new TrafficGenerator(options, occupancy, harness.Time, generatorLog);
        var check = new OccupancyConsistencyCheck(tracker, generator, options.MaxActivePeople);
        lateBound.Target = new CompositeElevatorEventSink([tracker, generator, check], NullLogger<CompositeElevatorEventSink>.Instance);

        var simulation = new TrafficSimulation(harness, tracker, generator, check, trackerLog, generatorLog);
        if (startLoop)
        {
            harness.Start();
        }

        return simulation;
    }

    /// <summary>Whether the generator placed everyone it will place and they have all left, with the car idle.</summary>
    public bool IsDone
    {
        get
        {
            var snapshot = Elevator.Snapshot();
            return !Generator.IsRunning
                && Generator.Statistics.Active == 0
                && snapshot.State == ElevatorState.Idle
                && snapshot.PendingCalls.IsEmpty;
        }
    }

    /// <summary>Advances one step and waits for the operation in progress to finish and for every press to be taken in.</summary>
    public async Task StepAsync()
    {
        var state = Elevator.State;
        var busy = state is ElevatorState.MovingUp or ElevatorState.MovingDown or ElevatorState.DoorOpen;
        var before = Probe.Count;

        Harness.Time.Advance(ControllerHarness.Step);

        await Probe.WaitUntilAsync(
            () => (!busy || Probe.Count > before) && Generator.Statistics.PressesInFlight == 0,
            "the step to settle");
    }

    public async Task StepAsync(int steps)
    {
        for (var step = 0; step < steps; step++)
        {
            await StepAsync();
        }
    }

    /// <summary>Steps until <see cref="IsDone"/>; fails with the generator's statistics if it never gets there.</summary>
    public async Task RunUntilDoneAsync(int maxSteps = 20_000)
    {
        for (var step = 0; step < maxSteps; step++)
        {
            if (IsDone)
            {
                return;
            }

            await StepAsync();
        }

        throw new TimeoutException($"Not done after {maxSteps} steps: {Generator.Statistics}, {Tracker.Totals}, {Elevator.Snapshot().State}");
    }

    public ValueTask DisposeAsync()
    {
        Generator.Dispose();
        return Harness.DisposeAsync();
    }

    /// <summary>
    /// Lets the composite be created before the sinks that need the harness's clock exist. It also
    /// serialises publishing, so a safety command published from the test thread never interleaves
    /// with the loop's events between the tracker, the generator and the check.
    /// </summary>
    private sealed class LateBoundSink : IElevatorEventSink
    {
        private readonly Lock _gate = new();

        public CompositeElevatorEventSink? Target { get; set; }

        public void Publish(ElevatorEvent elevatorEvent)
        {
            lock (_gate)
            {
                Target?.Publish(elevatorEvent);
            }
        }
    }
}

/// <summary>
/// After every event, compares where the generator's people are with what the tracker counts. With
/// the generator as the only source of calls they must agree exactly, at every moment.
/// </summary>
internal sealed class OccupancyConsistencyCheck(OccupancyTracker tracker, TrafficGenerator generator, int maxActivePeople) : IElevatorEventSink
{
    private readonly Lock _gate = new();
    private string? _firstMismatch;
    private int _eventsChecked;
    private int _mostLeftBehind;
    private int _mostRiding;

    public string? FirstMismatch
    {
        get
        {
            lock (_gate)
            {
                return _firstMismatch;
            }
        }
    }

    public int EventsChecked
    {
        get
        {
            lock (_gate)
            {
                return _eventsChecked;
            }
        }
    }

    public int MostLeftBehind
    {
        get
        {
            lock (_gate)
            {
                return _mostLeftBehind;
            }
        }
    }

    public int MostRiding
    {
        get
        {
            lock (_gate)
            {
                return _mostRiding;
            }
        }
    }

    public void Publish(ElevatorEvent elevatorEvent)
    {
        var totals = tracker.Totals;
        var people = generator.Statistics;
        lock (_gate)
        {
            _eventsChecked++;
            _mostLeftBehind = Math.Max(_mostLeftBehind, totals.LeftBehind);
            _mostRiding = Math.Max(_mostRiding, totals.Riding);
            var agrees = totals.Waiting + totals.LeftBehind == people.Waiting
                && totals.Riding == people.Riding
                && totals.Delivered == people.Delivered
                && totals.Evacuated == people.Evacuated
                && totals.Corrections == 0
                && people.Active <= maxActivePeople;
            if (!agrees && _firstMismatch is null)
            {
                _firstMismatch = $"after event {_eventsChecked} ({elevatorEvent}): tracker {totals}; generator {people}";
            }
        }
    }
}
