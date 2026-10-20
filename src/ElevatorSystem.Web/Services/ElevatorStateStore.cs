using ElevatorSystem.Contracts;

namespace ElevatorSystem.Web.Services;

/// <summary>
/// The single source of what the UI shows: the building configuration, the newest status the server sent,
/// a bounded log of recent activity and the connection state. Components subscribe to <see cref="Changed"/>
/// and render the snapshot; they never guess the elevator's state themselves.
/// </summary>
public sealed class ElevatorStateStore
{
    public const int DefaultActivityCapacity = 30;

    private readonly Lock _gate = new();
    private readonly LinkedList<ElevatorActivityDto> _activity = new();
    private readonly TimeProvider _timeProvider;
    private bool _resynchronising = true;

    public ElevatorStateStore(TimeProvider? timeProvider = null, int activityCapacity = DefaultActivityCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(activityCapacity, 1);
        _timeProvider = timeProvider ?? TimeProvider.System;
        ActivityCapacity = activityCapacity;
    }

    /// <summary>Raised after any change; handlers re-render from the store's properties.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised for each new activity entry, in order, so announcers can react to single steps.</summary>
    public event EventHandler<ElevatorActivityDto>? ActivityReceived;

    public int ActivityCapacity { get; }

    public ElevatorConfigurationDto? Configuration { get; private set; }

    public ElevatorStatusDto? Status { get; private set; }

    public ConnectionState Connection { get; private set; } = ConnectionState.Connecting;

    /// <summary>
    /// How far the server's clock runs ahead of this browser's (negative when it is behind), learnt from the statuses it
    /// sends; <see langword="null"/> before the first. Times the server sends, such as when the doors close, are compared
    /// with <see cref="ServerNow"/>, never with the browser's clock, which may be seconds off.
    /// </summary>
    public TimeSpan? ServerClockOffset { get; private set; }

    /// <summary>The server's time now, as well as this browser can tell.</summary>
    public DateTimeOffset ServerNow => _timeProvider.GetUtcNow() + (ServerClockOffset ?? TimeSpan.Zero);

    /// <summary>How many people are where; only staff connections receive it (ADR-0022).</summary>
    public BuildingOccupancyDto? Occupancy { get; private set; }

    /// <summary>Recent activity, newest first, never more than <see cref="ActivityCapacity"/> entries.</summary>
    public IReadOnlyList<ElevatorActivityDto> Activity
    {
        get
        {
            lock (_gate)
            {
                return [.. _activity];
            }
        }
    }

    public void SetConfiguration(ElevatorConfigurationDto configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        Configuration = configuration;
        OnChanged();
    }

    /// <summary>
    /// Shows <paramref name="status"/> unless an equal or newer version is already shown, so a late HTTP
    /// response or a reordered message can never move the car backwards.
    /// </summary>
    /// <returns><c>true</c> when the status was applied.</returns>
    public bool ApplyStatus(ElevatorStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);

        lock (_gate)
        {
            LearnClockOffset(status);

            // After the connection is (re)established, the first status comes from the server that is
            // live now. It wins even with a lower version: the API may have restarted and begun counting again.
            if (!_resynchronising && Status is { } shown && status.Version <= shown.Version)
            {
                return false;
            }

            Status = status;
            _resynchronising = false;
        }

        OnChanged();
        return true;
    }

    public void AddActivity(ElevatorActivityDto activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        lock (_gate)
        {
            _activity.AddFirst(activity);
            while (_activity.Count > ActivityCapacity)
            {
                _activity.RemoveLast();
            }
        }

        ActivityReceived?.Invoke(this, activity);
        OnChanged();
    }

    /// <summary>Shows <paramref name="occupancy"/> unless newer counts are already shown.</summary>
    /// <returns><c>true</c> when the counts were applied.</returns>
    public bool ApplyOccupancy(BuildingOccupancyDto occupancy)
    {
        ArgumentNullException.ThrowIfNull(occupancy);

        lock (_gate)
        {
            if (Occupancy is { } shown && occupancy.TakenAt < shown.TakenAt)
            {
                return false;
            }

            Occupancy = occupancy;
        }

        OnChanged();
        return true;
    }

    /// <summary>Forgets the counts, for example after signing out.</summary>
    public void ClearOccupancy()
    {
        lock (_gate)
        {
            Occupancy = null;
        }

        OnChanged();
    }

    public void SetConnection(ConnectionState state)
    {
        lock (_gate)
        {
            if (Connection == state)
            {
                return;
            }

            Connection = state;
            if (state != ConnectionState.Connected)
            {
                _resynchronising = true;
                // The server may be another instance with another clock.
                ServerClockOffset = null;
            }
        }

        OnChanged();
    }

    /// <summary>
    /// A status is taken just before it is sent, so its <c>TakenAt</c> minus the time it arrived is the clock offset less
    /// the delivery delay. The largest sample is the closest estimate: an older copy (a cached status fetched over HTTP)
    /// gives a smaller one and never lowers it.
    /// </summary>
    private void LearnClockOffset(ElevatorStatusDto status)
    {
        var sample = status.TakenAt - _timeProvider.GetUtcNow();
        if (ServerClockOffset is not { } offset || sample > offset)
        {
            ServerClockOffset = sample;
        }
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
