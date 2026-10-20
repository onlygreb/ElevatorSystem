using ElevatorSystem.Application.Passengers;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using Microsoft.Extensions.Logging;

namespace ElevatorSystem.Application.Simulation;

/// <summary>Settings of the demo scenarios (ADR-0027), bound from the <c>Simulation</c> configuration section.</summary>
public sealed record SimulationOptions
{
    public const string SectionName = "Simulation";

    /// <summary>Whether admins may start demo scenarios. Off unless a deployment turns it on.</summary>
    public bool Enabled { get; init; }

    /// <summary>How often the simulated passengers look around and act.</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Makes every run of a scenario identical (tests); a different run each time when absent.</summary>
    public int? Seed { get; init; }

    /// <summary>Throws if any setting is out of range.</summary>
    public void Validate() => ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(TickInterval, TimeSpan.Zero, nameof(TickInterval));
}

/// <summary>
/// Runs the demo scenarios admins start from the admin view (ADR-0027): simulated passengers who arrive on the lowest
/// floor and act like people, through the same <see cref="PassengerService"/> as everyone else. They call the car,
/// walk in when its door is open at their floor, press their floor, and walk out there; anyone left behind by a full
/// car calls again.
/// </summary>
/// <remarks>
/// Every passenger looks around once per <see cref="SimulationOptions.TickInterval"/> on a <see cref="TimeProvider"/>
/// timer, so tests drive whole scenarios in simulated time. Thread-safe: starting, stopping and the timer share one lock.
/// </remarks>
public sealed class DemoScenarioRunner : IDisposable
{
    private static readonly TimeSpan Stagger = TimeSpan.FromSeconds(1.2);

    private readonly Lock _gate = new();
    private readonly PassengerService _service;
    private readonly Elevator _elevator;
    private readonly SimulationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DemoScenarioRunner> _logger;
    private readonly Random _random;
    private readonly List<SimulatedPassenger> _passengers = [];
    private ITimer? _timer;

    public DemoScenarioRunner(PassengerService service, Elevator elevator, SimulationOptions options, TimeProvider timeProvider, ILogger<DemoScenarioRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(elevator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();

        _service = service;
        _elevator = elevator;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _random = options.Seed is { } seed ? new Random(seed) : new Random();
    }

    public bool IsEnabled => _options.Enabled;

    /// <summary>Simulated passengers still acting.</summary>
    public int Active
    {
        get
        {
            lock (_gate)
            {
                return _passengers.Count;
            }
        }
    }

    /// <summary>Starts <paramref name="scenario"/> alongside any already running.</summary>
    /// <returns>How many simulated passengers it adds; 0 when scenarios are disabled.</returns>
    public int Start(DemoScenario scenario)
    {
        if (!_options.Enabled)
        {
            PassengerLog.ScenariosDisabled(_logger, scenario);
            return 0;
        }

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            var added = Plan(scenario, now);
            _passengers.AddRange(added);
            _timer ??= _timeProvider.CreateTimer(_ => Tick(), null, _options.TickInterval, _options.TickInterval);
            PassengerLog.ScenarioStarted(_logger, scenario, added.Count);
            return added.Count;
        }
    }

    /// <summary>Stops every scenario and removes all simulated passengers, wherever they are.</summary>
    /// <returns>How many simulated passengers left the building.</returns>
    public int StopAll(Guid? requestId = null)
    {
        lock (_gate)
        {
            _passengers.Clear();
            _timer?.Dispose();
            _timer = null;
            var removed = _service.Registry.RemoveSimulated(requestId);
            PassengerLog.ScenariosStopped(_logger, removed);
            return removed;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>One look around for every simulated passenger. Public so tests can step without a timer.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            var snapshot = _elevator.Snapshot();
            foreach (var passenger in _passengers.ToList())
            {
                try
                {
                    if (passenger.Act(this, now, snapshot))
                    {
                        _passengers.Remove(passenger);
                    }
                }
                // One passenger's failure must not stop the others, nor the timer.
                catch (Exception exception)
                {
                    PassengerLog.SimulatedStepFailed(_logger, exception);
                    _passengers.Remove(passenger);
                }
            }

            if (_passengers.Count == 0)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }
    }

    private List<SimulatedPassenger> Plan(DemoScenario scenario, DateTimeOffset now)
    {
        var building = _elevator.Building;
        var lobby = building.LowestFloor;
        var plans = new List<SimulatedPassenger>();
        switch (scenario)
        {
            case DemoScenario.MorningRush:
                // A dozen people come in within a quarter of a minute and stay at their floors.
                for (var index = 0; index < 12; index++)
                {
                    plans.Add(new SimulatedPassenger(now + (Stagger * index * Jitter()), [new Leg(UpperFloor(lobby), TimeSpan.Zero)], retireWhenDone: false));
                }

                break;
            case DemoScenario.LunchBreak:
                // People ride up, stay a while, come back down to the lobby and go out for lunch.
                for (var index = 0; index < 8; index++)
                {
                    plans.Add(new SimulatedPassenger(
                        now + (Stagger * index * Jitter()),
                        [new Leg(UpperFloor(lobby), Seconds(8, 18)), new Leg(lobby, Seconds(1, 3))],
                        retireWhenDone: true));
                }

                break;
            case DemoScenario.BusyDay:
                // A steady stream over about a minute: each person visits a couple of floors and leaves from the lobby.
                for (var index = 0; index < 18; index++)
                {
                    var legs = new List<Leg>();
                    var at = lobby;
                    for (var stop = 0; stop < _random.Next(1, 4); stop++)
                    {
                        var next = OtherFloor(at);
                        legs.Add(new Leg(next, Seconds(3, 10)));
                        at = next;
                    }

                    legs.Add(new Leg(lobby, Seconds(1, 2)));
                    plans.Add(new SimulatedPassenger(now + (TimeSpan.FromSeconds(3.5) * index * Jitter()), legs, retireWhenDone: true));
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown demo scenario.");
        }

        return plans;
    }

    private Floor UpperFloor(Floor lobby) => new(_random.Next(lobby.Number + 1, _elevator.Building.HighestFloor.Number + 1));

    private Floor OtherFloor(Floor from)
    {
        var building = _elevator.Building;
        var floor = new Floor(_random.Next(building.LowestFloor.Number, building.HighestFloor.Number));
        return floor >= from ? new Floor(floor.Number + 1) : floor;
    }

    private TimeSpan Seconds(double from, double to) => TimeSpan.FromSeconds(from + (_random.NextDouble() * (to - from)));

    private double Jitter() => 0.75 + (_random.NextDouble() * 0.5);

    /// <summary>Where a simulated passenger goes next, and how long they stay there.</summary>
    private sealed record Leg(Floor Target, TimeSpan Stay);

    /// <summary>One simulated passenger: a plan of legs, followed at a human pace.</summary>
    private sealed class SimulatedPassenger(DateTimeOffset arrivesAt, List<Leg> legs, bool retireWhenDone)
    {
        private static readonly TimeSpan ButtonPress = TimeSpan.FromMilliseconds(600);

        private readonly Queue<Leg> _legs = new(legs);
        private bool _joined;
        private DateTimeOffset _readyAt = arrivesAt;
        private DateTimeOffset _nextActionAt = arrivesAt;

        public Guid Id { get; } = Guid.NewGuid();

        /// <returns><see langword="true"/> when this passenger is done acting.</returns>
        public bool Act(DemoScenarioRunner runner, DateTimeOffset now, ElevatorSnapshot snapshot)
        {
            if (now < _nextActionAt)
            {
                return false;
            }

            var service = runner._service;
            if (!_joined)
            {
                _joined = service.Join(Id, simulated: true).Succeeded;
                _readyAt = now + ButtonPress;
                _nextActionAt = now + ButtonPress;
                return !_joined;
            }

            if (service.Registry.Find(Id) is not { } me)
            {
                return true;
            }

            if (_legs.Count == 0)
            {
                if (retireWhenDone)
                {
                    service.Registry.Retire(Id);
                }

                return true;
            }

            var leg = _legs.Peek();
            var doorOpenHere = snapshot.State == ElevatorState.DoorOpen && snapshot.CurrentFloor.Number == me.Floor;
            if (!me.InCar)
            {
                if (me.Floor == leg.Target.Number)
                {
                    _legs.Dequeue();
                    _readyAt = now + leg.Stay;
                    _nextActionAt = _readyAt;
                    return false;
                }

                if (now < _readyAt)
                {
                    return false;
                }

                if (doorOpenHere && service.Board(Id).Succeeded)
                {
                    _nextActionAt = now + ButtonPress;
                    return false;
                }

                var direction = leg.Target.Number > me.Floor ? Direction.Up : Direction.Down;
                var callPending = snapshot.PendingCalls.Any(pending => pending.Call == new HallCall(new Floor(me.Floor), direction));
                if (!doorOpenHere && !callPending)
                {
                    // First call, or the car left without them (it was full): press the button (again).
                    service.CallElevator(Id, direction);
                    _nextActionAt = now + ButtonPress;
                }

                return false;
            }

            if (doorOpenHere && me.Floor == leg.Target.Number)
            {
                service.Leave(Id);
                _legs.Dequeue();
                _readyAt = now + leg.Stay;
                _nextActionAt = now + ButtonPress;
                return false;
            }

            var carCallPending = snapshot.PendingCalls.Any(pending => pending.Call == new CarCall(leg.Target));
            if (me.Destination != leg.Target.Number || (!carCallPending && !doorOpenHere))
            {
                service.ChooseFloor(Id, leg.Target);
                _nextActionAt = now + ButtonPress;
            }

            return false;
        }
    }
}
