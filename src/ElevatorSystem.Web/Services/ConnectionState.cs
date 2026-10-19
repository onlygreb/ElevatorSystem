namespace ElevatorSystem.Web.Services;

/// <summary>State of the real-time connection to the elevator hub, as the UI shows it.</summary>
public enum ConnectionState
{
    /// <summary>First attempt to connect since the page loaded.</summary>
    Connecting,

    /// <summary>Connected: statuses and activity arrive live.</summary>
    Connected,

    /// <summary>The connection dropped; retrying with backoff. The view shows the last known status.</summary>
    Reconnecting,
}
