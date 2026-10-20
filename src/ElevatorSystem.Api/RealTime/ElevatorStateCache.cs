using ElevatorSystem.Contracts;

namespace ElevatorSystem.Api.RealTime;

/// <summary>
/// The latest elevator status and passenger counts the engine reported, for <c>GET</c> requests and for hub
/// clients that connect. Fed by <see cref="ElevatorUpdateSubscriptionService"/>; empty until the first update.
/// </summary>
/// <remarks>
/// Updates are applied in the order they arrive. The broker keeps the order of one publisher, only the leader
/// publishes, and a new leader starts after the old one has stopped, so arrival order is the engine's order.
/// A redelivered update, or one the engine re-sends to keep late subscribers current, is recognised by its id
/// and ignored.
/// </remarks>
internal sealed class ElevatorStateCache(TimeProvider timeProvider)
{
    /// <summary>How many recent update ids are remembered to recognise copies.</summary>
    public const int RememberedUpdates = 256;

    private readonly Lock _gate = new();
    private readonly Queue<Guid> _recentIds = new();
    private readonly HashSet<Guid> _recentIdSet = [];
    private ElevatorStatusDto? _status;
    private BuildingOccupancyDto? _occupancy;
    private DateTimeOffset? _lastUpdateAt;
    private readonly Dictionary<Guid, PassengerDto> _passengers = [];

    /// <summary>The most passengers remembered; the engine caps the building lower, so this only bounds memory.</summary>
    public const int MaxPassengers = 5000;

    /// <summary>The newest status, or <see langword="null"/> before the engine reported one.</summary>
    public ElevatorStatusDto? Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>The newest passenger counts, or <see langword="null"/> before the engine reported them.</summary>
    public BuildingOccupancyDto? Occupancy
    {
        get
        {
            lock (_gate)
            {
                return _occupancy;
            }
        }
    }

    /// <summary>When the last update of either kind arrived, copies included: a sign the engine is alive.</summary>
    public DateTimeOffset? LastUpdateAt
    {
        get
        {
            lock (_gate)
            {
                return _lastUpdateAt;
            }
        }
    }

    /// <summary>Applies <paramref name="update"/>; returns <see langword="false"/> for a copy already applied.</summary>
    public bool TryApply(ElevatorUpdateV1 update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (_gate)
        {
            _lastUpdateAt = timeProvider.GetUtcNow();
            if (!Remember(update.UpdateId))
            {
                return false;
            }

            _status = update.Status;
            Follow(update.Activity);
            return true;
        }
    }

    /// <summary>Applies <paramref name="update"/>; returns <see langword="false"/> for a copy already applied.</summary>
    public bool TryApply(OccupancyUpdateV1 update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (_gate)
        {
            _lastUpdateAt = timeProvider.GetUtcNow();
            if (!Remember(update.UpdateId))
            {
                return false;
            }

            _occupancy = update.Occupancy;
            return true;
        }
    }

    /// <summary>Where a passenger is, as the engine last reported; <see langword="null"/> when unknown or gone.</summary>
    public PassengerDto? Passenger(Guid passengerId)
    {
        lock (_gate)
        {
            return _passengers.GetValueOrDefault(passengerId);
        }
    }

    /// <summary>Follows one passenger through the activity feed (ADR-0026).</summary>
    private void Follow(ElevatorActivityDto activity)
    {
        if (activity.PassengerId is not { } id)
        {
            return;
        }

        var simulated = activity.Simulated ?? false;
        var known = _passengers.GetValueOrDefault(id);
        PassengerDto? next = activity.Kind switch
        {
            ElevatorActivityKind.PassengerJoined => new PassengerDto(id, activity.Floor, false, null, null, simulated),
            ElevatorActivityKind.PassengerCalled when known is not null => known with { Waiting = activity.Direction },
            ElevatorActivityKind.PassengerBoarded when known is not null => known with { Floor = activity.Floor, InCar = true, Waiting = null },
            ElevatorActivityKind.PassengerChoseFloor when known is not null => known with { Destination = activity.Floor },
            ElevatorActivityKind.PassengerLeft when known is not null => known with { Floor = activity.Floor, InCar = false, Destination = null },
            ElevatorActivityKind.PassengerRemoved => null,
            ElevatorActivityKind.PassengerRefused when activity.PassengerRefusal == PassengerRefusalReason.UnknownPassenger => null,
            _ => known,
        };

        if (next is null)
        {
            _passengers.Remove(id);
        }
        else if (_passengers.Count < MaxPassengers || _passengers.ContainsKey(id))
        {
            _passengers[id] = next;
        }
    }

    private bool Remember(Guid updateId)
    {
        if (!_recentIdSet.Add(updateId))
        {
            return false;
        }

        _recentIds.Enqueue(updateId);
        if (_recentIds.Count > RememberedUpdates)
        {
            _recentIdSet.Remove(_recentIds.Dequeue());
        }

        return true;
    }
}
