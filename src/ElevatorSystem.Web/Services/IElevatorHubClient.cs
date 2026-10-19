using ElevatorSystem.Contracts;

namespace ElevatorSystem.Web.Services;

/// <summary>The real-time side: keeps a connection to <see cref="ElevatorHub.Path"/> and feeds the <see cref="ElevatorStateStore"/>.</summary>
public interface IElevatorHubClient
{
    /// <summary>Starts connecting in the background and keeps retrying; calling it again does nothing.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);
}
