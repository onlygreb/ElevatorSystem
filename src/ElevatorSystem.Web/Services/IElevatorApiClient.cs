using ElevatorSystem.Contracts;

namespace ElevatorSystem.Web.Services;

/// <summary>The REST side of the elevator API (<see cref="ApiRoutes"/>).</summary>
public interface IElevatorApiClient
{
    Task<ElevatorConfigurationDto> GetConfigurationAsync(CancellationToken cancellationToken = default);

    Task<ElevatorStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Hall call: someone on <paramref name="floor"/> wants to go <paramref name="direction"/>.</summary>
    Task<RequestOutcome> RequestElevatorAsync(int floor, CallDirection direction, CancellationToken cancellationToken = default);

    /// <summary>Car call: a passenger inside the car chose <paramref name="floor"/>.</summary>
    Task<RequestOutcome> RequestDestinationAsync(int floor, CancellationToken cancellationToken = default);
}
