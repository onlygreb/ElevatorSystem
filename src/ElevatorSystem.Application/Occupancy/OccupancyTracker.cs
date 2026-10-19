using ElevatorSystem.Application.Ports;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;
using Microsoft.Extensions.Logging;
using Log = ElevatorSystem.Application.Occupancy.OccupancyLog;

namespace ElevatorSystem.Application.Occupancy;

/// <summary>
/// Counts people waiting and present on each floor and riding the car, from the calls they place
/// (ADR-0017 §2, ADR-0020). Passengers are anonymous: only counts and call times are kept (ADR-0009).
/// </summary>
/// <remarks>
/// <para>The rules, applied to the elevator's events in order:</para>
/// <list type="bullet">
/// <item>A hall call (queued, merged, or served on the spot) is one more person waiting on its floor,
/// who stops being present there if anyone was. When someone on that floor was left behind, the call
/// is theirs, placed again, and nobody new is counted.</item>
/// <item>While the door is open at a floor, the people waiting there board in arrival order, up to
/// <see cref="OccupancyOptions.CarCapacity"/>. Anyone left over keeps waiting with no pending call and
/// has to call again.</item>
/// <item>A served car call lets that many riders off; they become present on the floor and count as
/// delivered. Riders get off before anyone boards, whatever order the events of one stop arrive in.</item>
/// <item>The wait runs from calling to boarding.</item>
/// <item>Going out of service evacuates the riders at the car's floor (present there, not delivered);
/// waiting people stay waiting, their calls cancelled, and must call again once it is back. Events
/// that a safety command overtook (published after the out-of-service event) move nobody.</item>
/// <item>Counts never go negative: inconsistent events are clamped and logged (event IDs 1800-1802).</item>
/// </list>
/// <para>
/// Thread-safe and allocation-light, because the control loop calls <see cref="Publish"/>. The
/// observer is told about every change under the tracker's lock, so it sees the counts in order.
/// </para>
/// </remarks>
public sealed class OccupancyTracker : IElevatorEventSink
{
    private readonly Lock _gate = new();
    private readonly Building _building;
    private readonly int _capacity;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OccupancyTracker> _logger;
    private readonly IOccupancyObserver? _observer;
    private readonly FloorCounts[] _floors;
    private readonly Dictionary<HallCall, int> _countedRequests = [];
    private Floor? _doorOpenAt;
    private bool _outOfService;
    private int _peopleCounted;
    private int _riding;
    private int _delivered;
    private int _evacuated;
    private int _boarded;
    private int _corrections;
    private long _totalWaitTicks;
    private bool _changed;

    public OccupancyTracker(
        Building building,
        OccupancyOptions options,
        TimeProvider timeProvider,
        ILogger<OccupancyTracker> logger,
        IOccupancyObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(building);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();

        _building = building;
        _capacity = options.CarCapacity;
        _timeProvider = timeProvider;
        _logger = logger;
        _observer = observer;
        _floors = [.. Enumerable.Range(0, building.FloorCount).Select(_ => new FloorCounts())];
    }

    /// <summary>The counts now, one entry per floor, lowest first.</summary>
    public BuildingOccupancyDto Current
    {
        get
        {
            lock (_gate)
            {
                return Snapshot();
            }
        }
    }

    /// <summary>Building-wide totals, including the split between waiting and left behind.</summary>
    public OccupancyTotals Totals
    {
        get
        {
            lock (_gate)
            {
                int waiting = 0, leftBehind = 0, present = 0;
                foreach (var floor in _floors)
                {
                    waiting += floor.Callers;
                    leftBehind += floor.Waiting.Count - floor.Callers;
                    present += floor.Present;
                }

                return new OccupancyTotals(
                    _peopleCounted, waiting, leftBehind, _riding, present, _delivered, _evacuated, _boarded, _corrections);
            }
        }
    }

    public void Publish(ElevatorEvent elevatorEvent)
    {
        ArgumentNullException.ThrowIfNull(elevatorEvent);

        lock (_gate)
        {
            if (!_building.Contains(elevatorEvent.Floor))
            {
                Log.FloorOutsideBuilding(_logger, elevatorEvent.GetType().Name, elevatorEvent.Floor);
                return;
            }

            _changed = false;
            switch (elevatorEvent)
            {
                case CallQueued { Request.Call: HallCall call } queued:
                    Called(call, queued.Request.RequestedAt);
                    break;
                case CallMerged { Request.Call: HallCall call } merged:
                    Called(call, merged.Request.RequestedAt);
                    break;
                case DoorsOpened opened:
                    DoorOpened(opened.Floor, opened.OccurredAt);
                    break;
                case CallServed { Call.Call: HallCall call } served:
                    HallCallServed(call, served);
                    break;
                case CallServed served:
                    CarCallServed(served);
                    break;
                case DoorsClosed or ElevatorMoved:
                    _doorOpenAt = null;
                    break;
                case ElevatorTakenOutOfService outOfService:
                    TakenOutOfService(outOfService);
                    break;
                case ElevatorReturnedToService:
                    _outOfService = false;
                    break;
            }

            if (_changed)
            {
                Notify();
            }
        }
    }

    private void Called(HallCall call, DateTimeOffset calledAt)
    {
        if (_outOfService)
        {
            // Only a request applied just before a safety command overtook it: the out-of-service
            // event already listed it as cancelled, and it was counted from there.
            return;
        }

        _countedRequests[call] = _countedRequests.GetValueOrDefault(call) + 1;
        var floor = At(call.Floor);
        CountCaller(floor, calledAt);
        floor.Callers++;
    }

    /// <summary>Someone on the floor called: a person left behind calling again, or one more person waiting.</summary>
    private void CountCaller(FloorCounts floor, DateTimeOffset calledAt)
    {
        if (floor.Waiting.Count > floor.Callers)
        {
            return;
        }

        if (floor.Present > 0)
        {
            floor.Present--;
        }
        else
        {
            _peopleCounted++;
        }

        floor.Waiting.Enqueue(calledAt);
        _changed = true;
    }

    private void DoorOpened(Floor number, DateTimeOffset at)
    {
        if (_outOfService)
        {
            // A safety command overtook this stop: the riders were already evacuated.
            return;
        }

        _doorOpenAt = number;
        Board(At(number), number, at);
    }

    private void HallCallServed(HallCall call, CallServed served)
    {
        if (_outOfService)
        {
            // A safety command overtook this stop: its people were counted and are waiting, left behind.
            return;
        }

        var floor = At(call.Floor);
        _countedRequests.Remove(call, out var counted);
        floor.Callers = Math.Max(0, floor.Callers - counted);

        var servedOnTheSpot = counted == 0 && served.Call.RequestCount == 1 && _doorOpenAt == call.Floor;
        var uncounted = served.Call.RequestCount - counted;
        if (uncounted > 0 && !servedOnTheSpot)
        {
            // Requests applied before the tracker started, for example restored after a restart.
            Log.UncountedRequests(_logger, call, uncounted);
            _corrections++;
        }
        else if (uncounted < 0)
        {
            Log.MissingRequests(_logger, call, served.Call.RequestCount, counted);
            _corrections++;
        }

        // Each uncounted request is a caller until the call is served: this keeps one person's
        // request from being mistaken for another's call placed again.
        for (var person = 0; person < uncounted; person++)
        {
            CountCaller(floor, served.Call.FirstRequestedAt);
            floor.Callers++;
        }

        floor.Callers = Math.Max(0, floor.Callers - Math.Max(0, uncounted));

        // A call is only served while the door is open at its floor.
        _doorOpenAt = call.Floor;
        Board(floor, call.Floor, served.OccurredAt);
    }

    private void CarCallServed(CallServed served)
    {
        if (_outOfService)
        {
            // A safety command overtook this stop: the riders were already evacuated.
            return;
        }

        _doorOpenAt = served.Floor;
        var leaving = served.Call.RequestCount;
        if (leaving > _riding)
        {
            Log.RidersClamped(_logger, served.Floor, leaving, _riding);
            _corrections++;
            leaving = _riding;
        }

        var floor = At(served.Floor);
        if (leaving > 0)
        {
            _riding -= leaving;
            floor.Present += leaving;
            _delivered += leaving;
            _changed = true;
        }

        // Whoever got off made room for people still waiting at this floor.
        Board(floor, served.Floor, served.OccurredAt);
    }

    private void Board(FloorCounts floor, Floor number, DateTimeOffset at)
    {
        if (_outOfService || _doorOpenAt != number)
        {
            return;
        }

        while (_riding < _capacity && floor.Waiting.TryDequeue(out var calledAt))
        {
            _riding++;
            _boarded++;
            _totalWaitTicks += Math.Max(0, (at - calledAt).Ticks);
            _changed = true;
        }

        floor.Callers = Math.Min(floor.Callers, floor.Waiting.Count);
    }

    private void TakenOutOfService(ElevatorTakenOutOfService outOfService)
    {
        _outOfService = true;
        _doorOpenAt = null;

        var riders = _riding;
        if (riders > 0)
        {
            At(outOfService.Floor).Present += riders;
            _evacuated += riders;
            _riding = 0;
            _changed = true;
        }

        foreach (var cancelled in outOfService.CancelledCalls)
        {
            if (cancelled.Call is not HallCall call || !_building.Contains(call.Floor))
            {
                continue;
            }

            var uncounted = cancelled.RequestCount - _countedRequests.GetValueOrDefault(call);
            if (uncounted > 0)
            {
                Log.UncountedRequests(_logger, call, uncounted);
                _corrections++;
                var floor = At(call.Floor);
                for (var person = 0; person < uncounted; person++)
                {
                    CountCaller(floor, cancelled.FirstRequestedAt);
                    floor.Callers++;
                }
            }
        }

        // Every call was cancelled: whoever is waiting has to call again.
        _countedRequests.Clear();
        var waiting = 0;
        foreach (var floor in _floors)
        {
            floor.Callers = 0;
            waiting += floor.Waiting.Count;
        }

        if (riders > 0 || waiting > 0)
        {
            Log.Evacuated(_logger, outOfService.Floor, riders, waiting);
        }
    }

    private void Notify()
    {
        if (_observer is null)
        {
            return;
        }

        try
        {
            _observer.OccupancyChanged(Snapshot());
        }
        catch (Exception exception)
        {
            Log.ObserverFailed(_logger, exception);
        }
    }

    private BuildingOccupancyDto Snapshot()
    {
        var floors = new FloorOccupancyDto[_floors.Length];
        for (var index = 0; index < floors.Length; index++)
        {
            var floor = _floors[index];
            floors[index] = new FloorOccupancyDto(_building.LowestFloor.Number + index, floor.Waiting.Count, floor.Present);
        }

        return new BuildingOccupancyDto(
            _riding,
            _capacity,
            floors,
            _delivered,
            _boarded == 0 ? null : TimeSpan.FromTicks(_totalWaitTicks / _boarded),
            _timeProvider.GetUtcNow());
    }

    private FloorCounts At(Floor floor) => _floors[floor.Number - _building.LowestFloor.Number];

    /// <summary>One floor: call times of the people waiting (oldest first) and how many of them have a pending call.</summary>
    private sealed class FloorCounts
    {
        public Queue<DateTimeOffset> Waiting { get; } = new();

        public int Callers { get; set; }

        public int Present { get; set; }
    }
}
