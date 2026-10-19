using System.Collections.Immutable;
using ElevatorSystem.Domain.Events;

namespace ElevatorSystem.Domain;

/// <summary>
/// One elevator car: where it is, what it is doing, and which calls it still has to serve.
/// </summary>
/// <remarks>
/// Every member is thread-safe. Each transition is an atomic check-and-set under a lock and returns
/// the events it produced; nothing is logged or published while the lock is held. A refused
/// transition says why and never changes state (ADR-0004, ADR-0007).
/// </remarks>
public sealed class Elevator
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly List<PendingCall> _pendingCalls = [];
    private Floor _currentFloor;
    private ElevatorState _state = ElevatorState.Idle;
    private Direction? _travelDirection;
    private OutOfServiceReason? _outOfServiceReason;
    private long _version;

    /// <summary>Creates an idle elevator at the building's lowest floor.</summary>
    public Elevator(Building building, TimeProvider timeProvider)
        : this(building, timeProvider, building?.LowestFloor ?? default)
    {
    }

    /// <summary>Creates an idle elevator at <paramref name="startFloor"/>.</summary>
    public Elevator(Building building, TimeProvider timeProvider, Floor startFloor)
    {
        ArgumentNullException.ThrowIfNull(building);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (!building.Contains(startFloor))
        {
            throw new ArgumentOutOfRangeException(nameof(startFloor), startFloor, "The start floor must be inside the building.");
        }

        Building = building;
        _timeProvider = timeProvider;
        _currentFloor = startFloor;
    }

    /// <summary>
    /// Recreates an elevator from a snapshot saved before a restart (ADR-0007). A car that was moving or
    /// had its door open comes back idle at the recorded floor: after a restart it has stopped. An
    /// out-of-service car stays out of service, so a fault survives a restart until it is cleared.
    /// Pending calls are restored in order; calls that are invalid for <paramref name="building"/>,
    /// duplicated, or without requests are dropped.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The recorded floor is outside <paramref name="building"/>.</exception>
    public static Elevator Restore(Building building, TimeProvider timeProvider, ElevatorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var elevator = new Elevator(building, timeProvider, snapshot.CurrentFloor)
        {
            _travelDirection = snapshot.TravelDirection,
            _version = snapshot.Version,
        };

        if (snapshot.State == ElevatorState.OutOfService)
        {
            elevator._state = ElevatorState.OutOfService;
            elevator._outOfServiceReason = snapshot.OutOfServiceReason ?? OutOfServiceReason.Fault;
            return elevator;
        }

        foreach (var pending in snapshot.PendingCalls)
        {
            if (pending.RequestCount > 0
                && building.Validate(pending.Call) is null
                && !elevator._pendingCalls.Exists(existing => existing.Call == pending.Call))
            {
                elevator._pendingCalls.Add(pending);
            }
        }

        return elevator;
    }

    public Building Building { get; }

    /// <summary>The floor the car is at, or travelling to while it is moving.</summary>
    public Floor CurrentFloor
    {
        get
        {
            lock (_gate)
            {
                return _currentFloor;
            }
        }
    }

    public ElevatorState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Floors the car still has to stop at, without duplicates, in the order they were first requested.</summary>
    public ImmutableArray<Floor> TargetFloors => Snapshot().TargetFloors;

    public ElevatorSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new ElevatorSnapshot(
                _currentFloor,
                _state,
                _travelDirection,
                _outOfServiceReason,
                [.. _pendingCalls],
                _version,
                _timeProvider.GetUtcNow());
        }
    }

    /// <summary>Moves the car up one floor. Refused when the door is open, at the top floor, or while moving down.</summary>
    public TransitionResult MoveUp() => Move(Direction.Up);

    /// <summary>Moves the car down one floor. Refused when the door is open, at the bottom floor, or while moving up.</summary>
    public TransitionResult MoveDown() => Move(Direction.Down);

    /// <summary>
    /// Stops the car at its current floor and opens the door, serving every pending call for that floor.
    /// </summary>
    public TransitionResult OpenDoor()
    {
        lock (_gate)
        {
            if (_state == ElevatorState.OutOfService)
            {
                return TransitionResult.Rejected(TransitionError.OutOfService);
            }

            if (_state == ElevatorState.DoorOpen)
            {
                return TransitionResult.Unchanged;
            }

            var now = _timeProvider.GetUtcNow();
            _state = ElevatorState.DoorOpen;
            _version++;

            var events = ImmutableArray.CreateBuilder<ElevatorEvent>();
            events.Add(new DoorsOpened(now, _currentFloor));
            for (var index = 0; index < _pendingCalls.Count;)
            {
                var pending = _pendingCalls[index];
                if (pending.Call.Floor == _currentFloor)
                {
                    _pendingCalls.RemoveAt(index);
                    events.Add(new CallServed(now, _currentFloor, pending, WaitTimeSince(pending.FirstRequestedAt, now)));
                }
                else
                {
                    index++;
                }
            }

            return new TransitionResult(null, events.ToImmutable());
        }
    }

    /// <summary>Closes the door, leaving the car idle at its floor. Does nothing unless the door is open.</summary>
    public TransitionResult CloseDoor()
    {
        lock (_gate)
        {
            if (_state == ElevatorState.OutOfService)
            {
                return TransitionResult.Rejected(TransitionError.OutOfService);
            }

            if (_state != ElevatorState.DoorOpen)
            {
                return TransitionResult.Unchanged;
            }

            _state = ElevatorState.Idle;
            _version++;
            return TransitionResult.Success(new DoorsClosed(_timeProvider.GetUtcNow(), _currentFloor));
        }
    }

    /// <summary>
    /// Adds a passenger request to the calls the car has to serve. A call that is already pending
    /// absorbs the request; a call for the floor where the door is open is served at once.
    /// </summary>
    public AddRequestResult AddRequest(PassengerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            var call = request.Call;
            var rejection = Building.Validate(call)
                ?? (_state == ElevatorState.OutOfService ? RequestRejectionReason.OutOfService : (RequestRejectionReason?)null);
            if (rejection is { } reason)
            {
                return new AddRequestResult(
                    AddRequestOutcome.Rejected,
                    reason,
                    [new RequestRejected(now, call.Floor, request, reason)]);
            }

            if (_state == ElevatorState.DoorOpen && call.Floor == _currentFloor)
            {
                _version++;
                var served = new PendingCall(call, request.Id, request.RequestedAt, 1);
                return new AddRequestResult(
                    AddRequestOutcome.ServedImmediately,
                    null,
                    [new CallServed(now, call.Floor, served, WaitTimeSince(request.RequestedAt, now))]);
            }

            var index = _pendingCalls.FindIndex(pending => pending.Call == call);
            if (index >= 0)
            {
                var existing = _pendingCalls[index];
                _pendingCalls[index] = existing with { RequestCount = existing.RequestCount + 1 };
                _version++;
                return new AddRequestResult(
                    AddRequestOutcome.Merged,
                    null,
                    [new CallMerged(now, call.Floor, request, index + 1)]);
            }

            _pendingCalls.Add(new PendingCall(call, request.Id, request.RequestedAt, 1));
            _version++;
            return new AddRequestResult(
                AddRequestOutcome.Queued,
                null,
                [new CallQueued(now, call.Floor, request, _pendingCalls.Count)]);
        }
    }

    /// <summary>
    /// Takes the car out of service immediately, from any state, cancelling every pending call.
    /// While out of service the car refuses movement, door operations and new requests.
    /// </summary>
    public TransitionResult TakeOutOfService(OutOfServiceReason reason)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown out-of-service reason.");
        }

        lock (_gate)
        {
            if (_state == ElevatorState.OutOfService && _outOfServiceReason == reason)
            {
                return TransitionResult.Unchanged;
            }

            var cancelled = _pendingCalls.ToImmutableArray();
            _pendingCalls.Clear();
            _state = ElevatorState.OutOfService;
            _outOfServiceReason = reason;
            _version++;
            return TransitionResult.Success(
                new ElevatorTakenOutOfService(_timeProvider.GetUtcNow(), _currentFloor, reason, cancelled));
        }
    }

    /// <summary>Returns an out-of-service car to service, idle with the door closed.</summary>
    public TransitionResult ReturnToService()
    {
        lock (_gate)
        {
            if (_state != ElevatorState.OutOfService)
            {
                return TransitionResult.Rejected(TransitionError.NotOutOfService);
            }

            _state = ElevatorState.Idle;
            _outOfServiceReason = null;
            _version++;
            return TransitionResult.Success(new ElevatorReturnedToService(_timeProvider.GetUtcNow(), _currentFloor));
        }
    }

    private TransitionResult Move(Direction direction)
    {
        lock (_gate)
        {
            var error = CheckMove(direction);
            if (error is not null)
            {
                return TransitionResult.Rejected(error.Value);
            }

            var from = _currentFloor;
            _currentFloor = direction == Direction.Up ? from.Above : from.Below;
            _state = direction == Direction.Up ? ElevatorState.MovingUp : ElevatorState.MovingDown;
            _travelDirection = direction;
            _version++;
            return TransitionResult.Success(new ElevatorMoved(_timeProvider.GetUtcNow(), _currentFloor, from, direction));
        }
    }

    private TransitionError? CheckMove(Direction direction) => _state switch
    {
        ElevatorState.OutOfService => TransitionError.OutOfService,
        ElevatorState.DoorOpen => TransitionError.DoorIsOpen,
        ElevatorState.MovingUp when direction == Direction.Down => TransitionError.MovingInOppositeDirection,
        ElevatorState.MovingDown when direction == Direction.Up => TransitionError.MovingInOppositeDirection,
        _ when !Building.CanTravel(_currentFloor, direction) =>
            direction == Direction.Up ? TransitionError.AtTopFloor : TransitionError.AtBottomFloor,
        _ => null,
    };

    private static TimeSpan WaitTimeSince(DateTimeOffset requestedAt, DateTimeOffset now) =>
        now > requestedAt ? now - requestedAt : TimeSpan.Zero;
}
