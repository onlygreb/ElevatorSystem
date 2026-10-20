using ElevatorSystem.Contracts;

namespace ElevatorSystem.Web.Services;

/// <summary>The real-time side: keeps a connection to <see cref="ElevatorHub.Path"/> and feeds the <see cref="ElevatorStateStore"/>.</summary>
public interface IElevatorHubClient
{
    /// <summary>Starts connecting in the background and keeps retrying; calling it again does nothing.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the connection and opens a new one, so the server sees the current session cookie: after signing in or
    /// out, the staff-only messages start or stop.
    /// </summary>
    Task RestartAsync(CancellationToken cancellationToken = default);
}
