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
    private bool _resynchronising = true;

    public ElevatorStateStore(int activityCapacity = DefaultActivityCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(activityCapacity, 1);
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
            }
        }

        OnChanged();
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
