using ElevatorSystem.Application.Ports;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;
using Microsoft.Extensions.Logging;

namespace ElevatorSystem.Application.Passengers;

/// <summary>The result of a passenger's step: done, or refused and why. <see cref="Floor"/> is where the passenger is.</summary>
public readonly record struct PassengerOutcome(PassengerRefusalReason? Refusal, Floor Floor)
{
    public bool Succeeded => Refusal is null;
}

/// <summary>
/// Where every anonymous passenger is (ADR-0026): the engine's single source of truth for who waits, rides and is
/// present on each floor. Passengers arrive on the lowest floor, call the car from their floor, walk in while its door
/// is open there, choose a floor, and walk out while the door is open. Nothing identifies a person: a passenger is a
/// random id the browser keeps (ADR-0024).
/// </summary>
/// <remarks>
/// <para>
/// Steps are checked against the elevator's live state, under this registry's lock, and the observer hears about
/// every change in order. The registry also listens to the elevator: when the car goes out of service its riders
/// walk out onto the floor where it stopped. Waiting passengers keep waiting when the car leaves without them; they
/// press the button again.
/// </para>
/// <para>A person who takes no step for <see cref="PassengerOptions.IdleTimeout"/> has left; simulated passengers stay until their scenario ends.</para>
/// </remarks>
public sealed class PassengerRegistry : IElevatorEventSink
{
    private readonly Lock _gate = new();
    private readonly Elevator _elevator;
    private readonly PassengerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PassengerRegistry> _logger;
    private readonly IPassengerObserver? _observer;
    private readonly Dictionary<Guid, Passenger> _passengers = [];
    private int _delivered;
    private int _boardingsAfterCall;
    private long _totalWaitTicks;

    public PassengerRegistry(
        Elevator elevator, PassengerOptions options, TimeProvider timeProvider, ILogger<PassengerRegistry> logger, IPassengerObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(elevator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();

        _elevator = elevator;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _observer = observer;
    }

    public PassengerOptions Options => _options;

    /// <summary>The counts now, one entry per floor, lowest first.</summary>
    public BuildingOccupancyDto Current
    {
        get
        {
            lock (_gate)
            {
                return Counts();
            }
        }
    }

    /// <summary>Every passenger now.</summary>
    public IReadOnlyList<PassengerDto> Passengers
    {
        get
        {
            lock (_gate)
            {
                var snapshot = _elevator.Snapshot();
                return [.. _passengers.Values.Select(passenger => passenger.ToDto(snapshot))];
            }
        }
    }

    /// <summary>One passenger, or <see langword="null"/> when they are not in the building.</summary>
    public PassengerDto? Find(Guid passengerId)
    {
        lock (_gate)
        {
            return _passengers.TryGetValue(passengerId, out var passenger) ? passenger.ToDto(_elevator.Snapshot()) : null;
        }
    }

    /// <summary>A passenger arrives on the lowest floor. Joining again with the same id changes nothing.</summary>
    public PassengerOutcome Join(Guid passengerId, bool simulated = false, Guid? requestId = null)
    {
        var lobby = _elevator.Building.LowestFloor;
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (_passengers.TryGetValue(passengerId, out var existing))
            {
                existing.LastSeen = now;
                return Done(existing, _elevator.Snapshot());
            }

            RemoveIdle(now);
            if (_passengers.Count >= _options.MaxPassengers)
            {
                Notify(new PassengerRefused(now, passengerId, lobby, simulated, requestId, PassengerAction.Join, PassengerRefusalReason.BuildingFull));
                return new PassengerOutcome(PassengerRefusalReason.BuildingFull, lobby);
            }

            var passenger = new Passenger(passengerId, lobby, simulated, now);
            _passengers.Add(passengerId, passenger);
            Notify(new PassengerJoined(now, passengerId, lobby, simulated, requestId));
            NotifyCounts();
            return new PassengerOutcome(null, lobby);
        }
    }

    /// <summary>A passenger on a floor presses the button for <paramref name="direction"/>; they wait until they board.</summary>
    /// <remarks>The caller places the hall call itself; if the elevator refuses it, <see cref="WithdrawCall"/> undoes this.</remarks>
    public PassengerOutcome Call(Guid passengerId, Direction direction, Guid? requestId = null) =>
        Step(passengerId, PassengerAction.CallElevator, requestId, (passenger, snapshot, now) =>
        {
            if (passenger.InCar)
            {
                return PassengerRefusalReason.InCar;
            }

            if (!_elevator.Building.CanTravel(passenger.Floor, direction))
            {
                return PassengerRefusalReason.InvalidRequest;
            }

            if (snapshot.State == ElevatorState.OutOfService)
            {
                return PassengerRefusalReason.OutOfService;
            }

            if (passenger.Waiting != direction)
            {
                passenger.CalledAt ??= now;
                passenger.Waiting = direction;
            }

            Notify(new PassengerCalled(now, passenger.Id, passenger.Floor, passenger.Simulated, requestId, direction));
            NotifyCounts();
            return null;
        });

    /// <summary>The elevator refused the call placed for this passenger: they are no longer waiting for it.</summary>
    public void WithdrawCall(Guid passengerId)
    {
        lock (_gate)
        {
            if (_passengers.TryGetValue(passengerId, out var passenger) && passenger.Waiting is not null)
            {
                passenger.Waiting = null;
                passenger.CalledAt = null;
                NotifyCounts();
            }
        }
    }

    /// <summary>A passenger walks into the car, which must have its door open at their floor and room for one more.</summary>
    public PassengerOutcome Board(Guid passengerId, Guid? requestId = null) =>
        Step(passengerId, PassengerAction.Board, requestId, (passenger, snapshot, now) =>
        {
            if (passenger.InCar)
            {
                return PassengerRefusalReason.InCar;
            }

            if (snapshot.State != ElevatorState.DoorOpen || snapshot.CurrentFloor != passenger.Floor)
            {
                return PassengerRefusalReason.DoorNotOpenHere;
            }

            if (Riders() >= _options.CarCapacity)
            {
                return PassengerRefusalReason.CarFull;
            }

            TimeSpan? wait = passenger.CalledAt is { } calledAt ? now - calledAt : null;
            if (wait is { } waited)
            {
                _boardingsAfterCall++;
                _totalWaitTicks += waited.Ticks;
            }

            passenger.InCar = true;
            passenger.BoardedAt = passenger.Floor;
            passenger.Waiting = null;
            passenger.CalledAt = null;
            Notify(new PassengerBoarded(now, passenger.Id, passenger.Floor, passenger.Simulated, requestId, wait));
            NotifyCounts();
            return null;
        });

    /// <summary>A passenger inside the car chooses <paramref name="floor"/>; the caller places the car call.</summary>
    public PassengerOutcome ChooseFloor(Guid passengerId, Floor floor, Guid? requestId = null) =>
        Step(passengerId, PassengerAction.ChooseFloor, requestId, (passenger, snapshot, now) =>
        {
            if (!passenger.InCar)
            {
                return PassengerRefusalReason.NotInCar;
            }

            if (!_elevator.Building.Contains(floor))
            {
                return PassengerRefusalReason.InvalidRequest;
            }

            if (snapshot.State == ElevatorState.OutOfService)
            {
                return PassengerRefusalReason.OutOfService;
            }

            passenger.Destination = floor;
            Notify(new PassengerChoseFloor(now, passenger.Id, snapshot.CurrentFloor, passenger.Simulated, requestId, floor));
            return null;
        });

    /// <summary>A passenger walks out of the car onto the floor where its door is open.</summary>
    public PassengerOutcome Leave(Guid passengerId, Guid? requestId = null) =>
        Step(passengerId, PassengerAction.Leave, requestId, (passenger, snapshot, now) =>
        {
            if (!passenger.InCar)
            {
                return PassengerRefusalReason.NotInCar;
            }

            if (snapshot.State != ElevatorState.DoorOpen)
            {
                return PassengerRefusalReason.DoorClosed;
            }

            GetOff(passenger, snapshot.CurrentFloor);
            Notify(new PassengerLeft(now, passenger.Id, passenger.Floor, passenger.Simulated, requestId));
            NotifyCounts();
            return null;
        });

    /// <summary>Removes every simulated passenger, wherever they are: a demo scenario was stopped.</summary>
    /// <returns>How many left.</returns>
    public int RemoveSimulated(Guid? requestId = null)
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            var snapshot = _elevator.Snapshot();
            var simulated = _passengers.Values.Where(passenger => passenger.Simulated).ToList();
            foreach (var passenger in simulated)
            {
                Remove(passenger, snapshot, now, requestId);
            }

            if (simulated.Count > 0)
            {
                NotifyCounts();
            }

            return simulated.Count;
        }
    }

    /// <summary>Removes one simulated passenger who reached the end of their scenario.</summary>
    public void Retire(Guid passengerId)
    {
        lock (_gate)
        {
            if (_passengers.TryGetValue(passengerId, out var passenger))
            {
                Remove(passenger, _elevator.Snapshot(), _timeProvider.GetUtcNow(), null);
                NotifyCounts();
            }
        }
    }

    /// <summary>Follows the elevator: when it goes out of service, its riders walk out where it stopped.</summary>
    public void Publish(ElevatorEvent elevatorEvent)
    {
        ArgumentNullException.ThrowIfNull(elevatorEvent);

        lock (_gate)
        {
            switch (elevatorEvent)
            {
                case ElevatorTakenOutOfService outOfService:
                    var riders = _passengers.Values.Where(passenger => passenger.InCar).ToList();
                    foreach (var rider in riders)
                    {
                        GetOff(rider, outOfService.Floor);
                        Notify(new PassengerLeft(outOfService.OccurredAt, rider.Id, rider.Floor, rider.Simulated, null));
                    }

                    if (riders.Count > 0)
                    {
                        PassengerLog.Evacuated(_logger, outOfService.Floor, riders.Count);
                    }

                    NotifyCounts();
                    break;
                case DoorsOpened:
                    if (RemoveIdle(elevatorEvent.OccurredAt) > 0)
                    {
                        NotifyCounts();
                    }

                    break;
            }
        }
    }

    private PassengerOutcome Step(
        Guid passengerId, PassengerAction action, Guid? requestId, Func<Passenger, ElevatorSnapshot, DateTimeOffset, PassengerRefusalReason?> apply)
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            var snapshot = _elevator.Snapshot();
            if (!_passengers.TryGetValue(passengerId, out var passenger))
            {
                var lobby = _elevator.Building.LowestFloor;
                Notify(new PassengerRefused(now, passengerId, lobby, false, requestId, action, PassengerRefusalReason.UnknownPassenger));
                return new PassengerOutcome(PassengerRefusalReason.UnknownPassenger, lobby);
            }

            passenger.LastSeen = now;
            if (apply(passenger, snapshot, now) is { } refusal)
            {
                var where = passenger.InCar ? snapshot.CurrentFloor : passenger.Floor;
                Notify(new PassengerRefused(now, passenger.Id, where, passenger.Simulated, requestId, action, refusal));
                PassengerLog.StepRefused(_logger, action, refusal);
                return new PassengerOutcome(refusal, where);
            }

            return Done(passenger, snapshot);
        }
    }

    private static PassengerOutcome Done(Passenger passenger, ElevatorSnapshot snapshot) =>
        new(null, passenger.InCar ? snapshot.CurrentFloor : passenger.Floor);

    private void GetOff(Passenger passenger, Floor floor)
    {
        passenger.InCar = false;
        passenger.Floor = floor;
        passenger.Destination = null;
        if (passenger.BoardedAt is { } boardedAt && boardedAt != floor)
        {
            _delivered++;
        }

        passenger.BoardedAt = null;
    }

    private void Remove(Passenger passenger, ElevatorSnapshot snapshot, DateTimeOffset now, Guid? requestId)
    {
        _passengers.Remove(passenger.Id);
        Notify(new PassengerRemoved(now, passenger.Id, passenger.InCar ? snapshot.CurrentFloor : passenger.Floor, passenger.Simulated, requestId));
    }

    private int RemoveIdle(DateTimeOffset now)
    {
        var cutoff = now - _options.IdleTimeout;
        var idle = _passengers.Values.Where(passenger => !passenger.Simulated && passenger.LastSeen < cutoff).ToList();
        var snapshot = _elevator.Snapshot();
        foreach (var passenger in idle)
        {
            Remove(passenger, snapshot, now, null);
        }

        if (idle.Count > 0)
        {
            PassengerLog.IdleRemoved(_logger, idle.Count);
        }

        return idle.Count;
    }

    private int Riders() => _passengers.Values.Count(passenger => passenger.InCar);

    private BuildingOccupancyDto Counts()
    {
        var building = _elevator.Building;
        var waiting = new int[building.FloorCount];
        var present = new int[building.FloorCount];
        var riding = 0;
        foreach (var passenger in _passengers.Values)
        {
            if (passenger.InCar)
            {
                riding++;
                continue;
            }

            var index = passenger.Floor.Number - building.LowestFloor.Number;
            if (passenger.Waiting is null)
            {
                present[index]++;
            }
            else
            {
                waiting[index]++;
            }
        }

        var floors = Enumerable.Range(0, building.FloorCount)
            .Select(index => new FloorOccupancyDto(building.LowestFloor.Number + index, waiting[index], present[index]))
            .ToList();
        TimeSpan? averageWait = _boardingsAfterCall == 0 ? null : TimeSpan.FromTicks(_totalWaitTicks / _boardingsAfterCall);
        return new BuildingOccupancyDto(riding, _options.CarCapacity, floors, _delivered, averageWait, _timeProvider.GetUtcNow());
    }

    private void Notify(PassengerEvent passengerEvent)
    {
        try
        {
            _observer?.PassengerChanged(passengerEvent);
        }
        // An observer is a hand-off to a buffer; its failure must never stop a passenger's step.
        catch (Exception exception)
        {
            PassengerLog.ObserverFailed(_logger, exception);
        }
    }

    private void NotifyCounts()
    {
        try
        {
            _observer?.OccupancyChanged(Counts());
        }
        catch (Exception exception)
        {
            PassengerLog.ObserverFailed(_logger, exception);
        }
    }

    private sealed class Passenger(Guid id, Floor floor, bool simulated, DateTimeOffset joinedAt)
    {
        public Guid Id { get; } = id;

        public bool Simulated { get; } = simulated;

        /// <summary>The floor they are on; while riding, the floor they boarded on (the car's floor is the live one).</summary>
        public Floor Floor { get; set; } = floor;

        public bool InCar { get; set; }

        public Floor? BoardedAt { get; set; }

        public Direction? Waiting { get; set; }

        public DateTimeOffset? CalledAt { get; set; }

        public Floor? Destination { get; set; }

        public DateTimeOffset LastSeen { get; set; } = joinedAt;

        public PassengerDto ToDto(ElevatorSnapshot snapshot) => new(
            Id,
            InCar ? snapshot.CurrentFloor.Number : Floor.Number,
            InCar,
            Waiting is { } direction ? ElevatorContractMapper.ToContract(direction) : null,
            Destination?.Number,
            Simulated);
    }
}
