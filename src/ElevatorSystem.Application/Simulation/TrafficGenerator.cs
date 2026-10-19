using ElevatorSystem.Application.Occupancy;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;
using Microsoft.Extensions.Logging;
using Log = ElevatorSystem.Application.Simulation.TrafficGeneratorLog;

namespace ElevatorSystem.Application.Simulation;

/// <summary>
/// Simulated passengers for tests and load runs (ADR-0017 §4, ADR-0020). People appear on random floors
/// at a random rate, press the hall button through <see cref="ElevatorController"/> exactly as a person
/// would, board when the door opens at their floor, press their destination, and get off there.
/// </summary>
/// <remarks>
/// <para>
/// Off by default (<see cref="TrafficGeneratorOptions.Enabled"/>). Time comes from the
/// <see cref="TimeProvider"/> and every random draw from one seeded <see cref="ArrivalSchedule"/>, so
/// a seed gives the same people at the same simulated times.
/// </para>
/// <para>
/// The generator must receive the elevator's events (add it to the controller's
/// <see cref="CompositeElevatorEventSink"/>), because its people react to them: it follows the same
/// boarding rules as <see cref="OccupancyTracker"/>, with the same car capacity, so when it is the only
/// source of calls its people are exactly where the tracker counts them. That includes being left
/// behind by a full car and calling again after the door closes, and a call placed at a floor where
/// someone was left behind counting as theirs (the person who pressed then presses again).
/// </para>
/// <para>Thread-safe: arrivals come from a timer and events from the control loop.</para>
/// </remarks>
public sealed class TrafficGenerator : IElevatorEventSink, IDisposable
{
    private readonly Lock _gate = new();
    private readonly TrafficGeneratorOptions _options;
    private readonly int _capacity;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TrafficGenerator> _logger;
    private readonly List<Person> _people = [];
    private readonly Dictionary<RequestId, Person> _presses = [];
    private ElevatorController? _controller;
    private ArrivalSchedule? _schedule;
    private ITimer? _timer;
    private SimulatedArrival? _next;
    private DateTimeOffset _nextDueAt;
    private Floor? _doorOpenAt;
    private bool _outOfService;
    private bool _stopped;
    private long _order;
    private int _arrivals;
    private int _skipped;
    private int _abandoned;
    private int _delivered;
    private int _evacuated;

    public TrafficGenerator(
        TrafficGeneratorOptions options,
        OccupancyOptions occupancy,
        TimeProvider timeProvider,
        ILogger<TrafficGenerator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(occupancy);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();
        occupancy.Validate();

        _options = options;
        _capacity = occupancy.CarCapacity;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Whether arrivals are still being scheduled.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _controller is not null && !_stopped && _next is not null;
            }
        }
    }

    public TrafficStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                int calling = 0, waiting = 0, leftBehind = 0, riding = 0;
                foreach (var person in _people)
                {
                    if (person.Riding)
                    {
                        riding++;
                    }
                    else if (!person.Counted)
                    {
                        calling++;
                    }
                    else
                    {
                        waiting++;
                        leftBehind += person.CallDirection is null ? 1 : 0;
                    }
                }

                return new TrafficStatistics(
                    _arrivals, _skipped, _abandoned, calling, waiting, leftBehind, riding, _delivered, _evacuated, _presses.Count);
            }
        }
    }

    /// <summary>
    /// Starts placing calls on <paramref name="controller"/>, unless the options leave the generator
    /// disabled. The controller must publish its events to this generator.
    /// </summary>
    /// <returns>Whether the generator started.</returns>
    /// <exception cref="InvalidOperationException">The generator was already started.</exception>
    public bool Start(ElevatorController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (!_options.Enabled)
        {
            Log.Disabled(_logger);
            return false;
        }

        lock (_gate)
        {
            if (_controller is not null)
            {
                throw new InvalidOperationException("The traffic generator was already started.");
            }

            _controller = controller;
            _schedule = new ArrivalSchedule(controller.Elevator.Building, _options);
            Log.Started(_logger, _options.ArrivalsPerMinute, _options.MaxActivePeople, _options.Seed);
            if (_options.MaxPeople == 0)
            {
                return true;
            }

            _next = _schedule.Next();
            _nextDueAt = _timeProvider.GetUtcNow() + _next.Delay;
            _timer = _timeProvider.CreateTimer(_ => OnArrivalDue(), null, _next.Delay, Timeout.InfiniteTimeSpan);
        }

        return true;
    }

    /// <summary>Stops new arrivals. People already in the building carry on until they get off.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            _timer?.Dispose();
            if (_controller is not null)
            {
                Log.Stopped(_logger, _arrivals);
            }
        }
    }

    public void Dispose() => Stop();

    public void Publish(ElevatorEvent elevatorEvent)
    {
        ArgumentNullException.ThrowIfNull(elevatorEvent);

        lock (_gate)
        {
            if (_controller is null)
            {
                return;
            }

            switch (elevatorEvent)
            {
                case CallQueued queued:
                    Acknowledged(queued.Request);
                    break;
                case CallMerged merged:
                    Acknowledged(merged.Request);
                    break;
                case RequestRejected rejected:
                    Rejected(rejected.Request);
                    break;
                case DoorsOpened opened when !_outOfService:
                    _doorOpenAt = opened.Floor;
                    Board(opened.Floor);
                    break;
                case CallServed { Call.Call: HallCall call } served:
                    HallCallServed(call, served.Call);
                    break;
                case CallServed served:
                    CarCallServed(served);
                    break;
                case DoorsClosed:
                    _doorOpenAt = null;
                    // On the loop thread: these presses are taken in before the next stop.
                    PressAgain(countedLimit: int.MaxValue);
                    break;
                case ElevatorMoved:
                    _doorOpenAt = null;
                    break;
                case ElevatorTakenOutOfService:
                    TakenOutOfService();
                    break;
                case ElevatorReturnedToService:
                    _outOfService = false;
                    // Not on the loop thread: one counted person calls the car back; the rest press when its door closes.
                    PressAgain(countedLimit: 1);
                    break;
            }
        }
    }

    private void OnArrivalDue()
    {
        lock (_gate)
        {
            if (_stopped || _next is null || _schedule is null)
            {
                return;
            }

            // Arrivals keep their own clock, so a late or coarse timer never changes when people appear.
            var now = _timeProvider.GetUtcNow();
            while (_next is not null && _nextDueAt <= now)
            {
                try
                {
                    Arrive(_next);
                }
                // A test tool must never take the elevator down with it.
                catch (Exception exception)
                {
                    Log.ArrivalFailed(_logger, exception);
                }

                if (_options.MaxPeople is { } maxPeople && _arrivals >= maxPeople)
                {
                    Log.AllPlaced(_logger, maxPeople);
                    _next = null;
                    break;
                }

                _next = _schedule.Next();
                _nextDueAt += _next.Delay;
            }

            // From the timer: only people the counts do not include yet, and destinations.
            PressAgain(countedLimit: 0);
            if (_next is not null)
            {
                _timer?.Change(_nextDueAt - now, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Arrive(SimulatedArrival arrival)
    {
        if (_people.Count >= _options.MaxActivePeople)
        {
            _skipped++;
            Log.ArrivalSkipped(_logger, _people.Count);
            return;
        }

        _arrivals++;
        var person = new Person(arrival.Origin, arrival.Destination, arrival.Direction);
        if (PressHallButton(person))
        {
            _people.Add(person);
        }
        else
        {
            _abandoned++;
        }
    }

    private bool PressHallButton(Person person)
    {
        var result = _controller!.RequestElevator(person.Origin.Number, person.Direction);
        if (result.RequestId is not { } id)
        {
            return false;
        }

        person.HallPress = id;
        _presses[id] = person;
        return true;
    }

    private void PressDestination(Person person)
    {
        var result = _controller!.RequestDestination(person.Destination.Number);
        person.NeedsDestination = result.RequestId is null;
        if (result.RequestId is { } id)
        {
            person.CarPress = id;
            _presses[id] = person;
        }
    }

    /// <summary>
    /// Everyone whose call is not pending presses again, unless the door is open where they wait.
    /// Only <paramref name="countedLimit"/> of them may be people the counts already include: such a
    /// press must reach the control loop before the door can open at their floor, or they would board
    /// with it still in flight and the loop would later take it for someone new. The loop's own events
    /// guarantee that (it takes requests in before its next stop); a timer or another thread does not.
    /// </summary>
    private void PressAgain(int countedLimit)
    {
        if (_outOfService)
        {
            return;
        }

        foreach (var person in _people)
        {
            if (person.Riding)
            {
                if (person.NeedsDestination)
                {
                    PressDestination(person);
                }
            }
            else if (person.HallPress is null
                && person.CallDirection is null
                && _doorOpenAt != person.Origin
                && (!person.Counted || countedLimit-- > 0))
            {
                PressHallButton(person);
            }
        }
    }

    /// <summary>The control loop took a press in; the hall call rules match <see cref="OccupancyTracker"/>'s.</summary>
    private void Acknowledged(PassengerRequest request)
    {
        if (!_presses.Remove(request.Id, out var person))
        {
            return;
        }

        if (request.Call is CarCall)
        {
            person.CarPress = null;
            person.DestinationAcknowledged = person.Riding;
            return;
        }

        person.HallPress = null;
        if (person.Riding)
        {
            return;
        }

        if (_outOfService)
        {
            // A press overtaken by a safety command: its call was cancelled with the others, and the
            // tracker counted it from the cancelled calls as one more person, left behind.
            if (!person.Counted)
            {
                Count(person);
            }

            return;
        }

        if (person.Counted)
        {
            person.CallDirection = person.Direction;
        }
        else if (LeftBehindAt(person.Origin) is { } leftBehind)
        {
            // Counted as the call of someone left behind; this person will press again.
            leftBehind.CallDirection = person.Direction;
        }
        else
        {
            Count(person);
            person.CallDirection = person.Direction;
        }
    }

    private void Rejected(PassengerRequest request)
    {
        if (!_presses.Remove(request.Id, out var person))
        {
            return;
        }

        if (request.Call is CarCall)
        {
            person.CarPress = null;
            person.NeedsDestination = person.Riding;
            return;
        }

        person.HallPress = null;
        if (!person.Counted && !person.Riding)
        {
            _people.Remove(person);
            _abandoned++;
        }
    }

    private void HallCallServed(HallCall call, PendingCall served)
    {
        if (_presses.Remove(served.FirstRequestId, out var person))
        {
            // Served on the spot: the press was taken in and its call served at once. When a safety
            // command overtook it, the tracker ignores it, so the person stays uncounted and presses again.
            person.HallPress = null;
            if (!person.Counted && !person.Riding && !_outOfService && LeftBehindAt(call.Floor) is null)
            {
                Count(person);
            }
        }

        if (_outOfService)
        {
            return;
        }

        foreach (var waiting in _people)
        {
            if (waiting.Origin == call.Floor && waiting.CallDirection == call.Direction)
            {
                waiting.CallDirection = null;
            }
        }

        _doorOpenAt = call.Floor;
        Board(call.Floor);
    }

    private void CarCallServed(CallServed served)
    {
        if (_presses.Remove(served.Call.FirstRequestId, out var pressed))
        {
            pressed.CarPress = null;
            pressed.DestinationAcknowledged = pressed.Riding;
        }

        if (_outOfService)
        {
            return;
        }

        _doorOpenAt = served.Floor;
        var leaving = _people
            .Where(person => person.Riding && person.DestinationAcknowledged && person.Destination == served.Floor)
            .OrderBy(person => person.Order)
            .Take(served.Call.RequestCount)
            .ToList();
        foreach (var person in leaving)
        {
            Leave(person);
            _delivered++;
        }

        Board(served.Floor);
    }

    private void Board(Floor floor)
    {
        if (_outOfService || _doorOpenAt != floor)
        {
            return;
        }

        var room = _capacity - _people.Count(person => person.Riding);
        if (room <= 0)
        {
            return;
        }

        var boarding = _people
            .Where(person => !person.Riding && person.Counted && person.Origin == floor)
            .OrderBy(person => person.Order)
            .Take(room)
            .ToList();
        foreach (var person in boarding)
        {
            if (person.HallPress is { } press)
            {
                _presses.Remove(press);
                person.HallPress = null;
            }

            person.Riding = true;
            person.CallDirection = null;
            PressDestination(person);
        }
    }

    private void TakenOutOfService()
    {
        _outOfService = true;
        _doorOpenAt = null;
        foreach (var person in _people.Where(person => person.Riding).ToList())
        {
            Leave(person);
            _evacuated++;
        }

        foreach (var person in _people)
        {
            person.CallDirection = null;
        }
    }

    private void Leave(Person person)
    {
        _people.Remove(person);
        if (person.CarPress is { } press)
        {
            _presses.Remove(press);
        }

        if (person.HallPress is { } hallPress)
        {
            _presses.Remove(hallPress);
        }
    }

    private void Count(Person person)
    {
        person.Counted = true;
        person.Order = ++_order;
    }

    private Person? LeftBehindAt(Floor floor) =>
        _people.Find(person => !person.Riding && person.Counted && person.CallDirection is null && person.Origin == floor);

    /// <summary>One simulated person. Anonymous: a floor, a destination and their progress, nothing else.</summary>
    private sealed class Person(Floor origin, Floor destination, Direction direction)
    {
        public Floor Origin { get; } = origin;

        public Floor Destination { get; } = destination;

        public Direction Direction { get; } = direction;

        /// <summary>Whether the occupancy counts include this person (as waiting, until they board).</summary>
        public bool Counted { get; set; }

        /// <summary>The hall call counted as this person's while it is pending; null when they have none.</summary>
        public Direction? CallDirection { get; set; }

        /// <summary>Arrival order among counted people, which is boarding order.</summary>
        public long Order { get; set; }

        public bool Riding { get; set; }

        public RequestId? HallPress { get; set; }

        public RequestId? CarPress { get; set; }

        public bool DestinationAcknowledged { get; set; }

        public bool NeedsDestination { get; set; }
    }
}
