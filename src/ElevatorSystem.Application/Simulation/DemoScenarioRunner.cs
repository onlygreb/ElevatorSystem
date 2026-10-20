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

    /// <summary>How many simulated passengers the huge load brings in (ADR-0031).</summary>
    public int HugeLoadPassengers { get; init; } = 120;

    /// <summary>How many times faster than normal the car and every simulation run while a huge load is on (ADR-0031).</summary>
    public double HugeLoadSpeed { get; init; } = 8;

    /// <summary>Throws if any setting is out of range.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(TickInterval, TimeSpan.Zero, nameof(TickInterval));
        ArgumentOutOfRangeException.ThrowIfLessThan(HugeLoadPassengers, 1, nameof(HugeLoadPassengers));
        ArgumentOutOfRangeException.ThrowIfLessThan(HugeLoadSpeed, 1.0, nameof(HugeLoadSpeed));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(HugeLoadSpeed, ElevatorController.MaxSpeed, nameof(HugeLoadSpeed));
    }
}

/// <summary>
/// Runs the demo scenarios admins start from the admin view (ADR-0027): simulated passengers who arrive on the lowest
/// floor and act like people, through the same <see cref="PassengerService"/> as everyone else. They call the car,
/// walk in when its door is open at their floor, press their floor, and walk out there. Someone who just walked out, or
/// found the car full, waits for it to leave that floor before trying again, and calls again then.
/// </summary>
/// <remarks>
/// <para>
/// Every passenger looks around once per <see cref="SimulationOptions.TickInterval"/> on a <see cref="TimeProvider"/>
/// timer, so tests drive whole scenarios in simulated time. Thread-safe: starting, stopping and the timer share one lock.
/// </para>
/// <para>
/// While a huge load is on, the car and the simulation run <see cref="SimulationOptions.HugeLoadSpeed"/> times faster
/// (ADR-0031): the passengers follow their own clock, which runs that much faster than the real one, and look around
/// that much more often. The speed returns to normal when the last of them is done, or the scenarios stop.
/// </para>
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
    private double _speed = 1;
    private DateTimeOffset _clock;
    private DateTimeOffset _clockReadAt;

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
        _clockReadAt = timeProvider.GetUtcNow();
        _clock = _clockReadAt;
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

    /// <summary>How many times faster than normal the simulation runs now: 1, or more while a huge load is on.</summary>
    public double Speed
    {
        get
        {
            lock (_gate)
            {
                return _speed;
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
            var added = Plan(scenario, ReadClock());
            _passengers.AddRange(added);
            AdjustSpeed();
            PassengerLog.ScenarioStarted(_logger, scenario, added.Count);
            return added.Count;
        }
    }

    /// <summary>
    /// Stops every scenario and removes all simulated passengers, wherever they are; clears the queue, keeping only the
    /// calls of people still in the building, and sends the car to the lowest floor (ADR-0032).
    /// </summary>
    /// <returns>How many simulated passengers left the building.</returns>
    public int StopAll(Guid? requestId = null)
    {
        lock (_gate)
        {
            _passengers.Clear();
            var removed = _service.Registry.RemoveSimulated(requestId);
            var keep = _service.Registry.Passengers.Where(passenger => !passenger.Simulated).SelectMany(CallsOf).ToList();
            if (!_service.Controller.ClearRequests(keep, _elevator.Building.LowestFloor))
            {
                PassengerLog.ClearRefused(_logger);
            }

            AdjustSpeed();
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
            var now = ReadClock();
            var look = new LookAround(_elevator.Snapshot(), _service.Registry.Current.PassengersInCar, _service.Registry.Options.CarCapacity);
            foreach (var passenger in _passengers.ToList())
            {
                try
                {
                    if (passenger.Act(this, now, look))
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

            AdjustSpeed();
        }
    }

    private TimeSpan TickPeriod => _options.TickInterval / _speed;

    /// <summary>The calls a person in the building is waiting on: their hall call, or the floor they chose in the car.</summary>
    private static IEnumerable<ElevatorCall> CallsOf(PassengerDto passenger)
    {
        if (passenger.Waiting is { } direction)
        {
            yield return new HallCall(new Floor(passenger.Floor), ElevatorContractMapper.ToDomain(direction));
        }

        if (passenger is { InCar: true, Destination: { } destination })
        {
            yield return new CarCall(new Floor(destination));
        }
    }

    /// <summary>The simulation's clock: it runs <see cref="_speed"/> times faster than the real one.</summary>
    private DateTimeOffset ReadClock()
    {
        var now = _timeProvider.GetUtcNow();
        _clock += (now - _clockReadAt) * _speed;
        _clockReadAt = now;
        return _clock;
    }

    /// <summary>Fast-forwards the car and the simulation while a huge load is on, and back to normal after it; keeps the timer running while anyone acts.</summary>
    private void AdjustSpeed()
    {
        var wanted = _passengers.Any(passenger => passenger.FastForward) ? _options.HugeLoadSpeed : 1;
        if (wanted != _speed)
        {
            ReadClock();
            // A real drive cannot be fast-forwarded; then the simulation keeps the normal pace too.
            _speed = _service.Controller.SetSpeed(wanted) ? wanted : 1;
            _timer?.Change(TickPeriod, TickPeriod);
        }

        if (_passengers.Count == 0)
        {
            _timer?.Dispose();
            _timer = null;
        }
        else
        {
            _timer ??= _timeProvider.CreateTimer(_ => Tick(), null, TickPeriod, TickPeriod);
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
            case DemoScenario.HugeLoad:
                // A crowd pours in within a minute, rides up, stays a moment and comes back down to leave; half of them
                // keep pressing the call button while they wait. Everything runs fast-forwarded until the last one is gone.
                var count = _options.HugeLoadPassengers;
                for (var index = 0; index < count; index++)
                {
                    plans.Add(new SimulatedPassenger(
                        now + (TimeSpan.FromSeconds(60.0 * index / count) * Jitter()),
                        [new Leg(UpperFloor(lobby), Seconds(2, 8)), new Leg(lobby, Seconds(1, 2))],
                        retireWhenDone: true,
                        impatient: _random.NextDouble() < 0.5,
                        fastForward: true));
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

    /// <summary>What every simulated passenger sees in one look around: the car, and how full it is as they walk in and out.</summary>
    private sealed class LookAround(ElevatorSnapshot snapshot, int riders, int capacity)
    {
        public ElevatorSnapshot Snapshot { get; } = snapshot;

        public int Riders { get; set; } = riders;

        public bool CarFull => Riders >= capacity;
    }

    /// <summary>
    /// One simulated passenger: a plan of legs, followed at a human pace. An impatient one presses the call button again
    /// every second or so while waiting; one that fast-forwards keeps the whole simulation fast while it acts.
    /// </summary>
    private sealed class SimulatedPassenger(DateTimeOffset arrivesAt, List<Leg> legs, bool retireWhenDone, bool impatient = false, bool fastForward = false)
    {
        private static readonly TimeSpan ButtonPress = TimeSpan.FromMilliseconds(600);
        private static readonly TimeSpan Impatience = TimeSpan.FromSeconds(2);

        private readonly Queue<Leg> _legs = new(legs);
        private bool _joined;
        private bool _waitForTheCarToLeave;
        private DateTimeOffset _readyAt = arrivesAt;
        private DateTimeOffset _nextActionAt = arrivesAt;

        public Guid Id { get; } = Guid.NewGuid();

        public bool FastForward => fastForward;

        /// <returns><see langword="true"/> when this passenger is done acting.</returns>
        public bool Act(DemoScenarioRunner runner, DateTimeOffset now, LookAround look)
        {
            var snapshot = look.Snapshot;
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

                if (_waitForTheCarToLeave)
                {
                    // Just walked out here, or the car was full: try again once it has left this floor. A car waiting
                    // here with nothing at all to do will not leave, so then they go ahead.
                    var carStillHere = snapshot.CurrentFloor.Number == me.Floor
                        && snapshot.State is ElevatorState.DoorOpen or ElevatorState.Idle
                        && !snapshot.PendingCalls.IsEmpty;
                    if (carStillHere)
                    {
                        return false;
                    }

                    _waitForTheCarToLeave = false;
                }

                if (doorOpenHere)
                {
                    if (look.CarFull || !service.Board(Id).Succeeded)
                    {
                        _waitForTheCarToLeave = true;
                        return false;
                    }

                    look.Riders++;
                    _nextActionAt = now + ButtonPress;
                    return false;
                }

                var direction = leg.Target.Number > me.Floor ? Direction.Up : Direction.Down;
                var callPending = snapshot.PendingCalls.Any(pending => pending.Call == new HallCall(new Floor(me.Floor), direction));
                if (!callPending)
                {
                    // First call, or the car left without them: press the button (again).
                    service.CallElevator(Id, direction);
                    _nextActionAt = now + ButtonPress;
                }
                else if (impatient)
                {
                    // Pressing again does not bring the car sooner: the controller merges every press into the call.
                    service.PressCallButton(new Floor(me.Floor), direction);
                    _nextActionAt = now + (Impatience * runner.Jitter());
                }

                return false;
            }

            if (doorOpenHere && me.Floor == leg.Target.Number)
            {
                // The look around may be a moment old: if the doors just closed, stay in and try again at the next stop.
                if (!service.Leave(Id).Succeeded)
                {
                    return false;
                }

                look.Riders--;
                _waitForTheCarToLeave = true;
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
