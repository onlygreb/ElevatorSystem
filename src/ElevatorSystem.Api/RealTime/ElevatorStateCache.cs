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
