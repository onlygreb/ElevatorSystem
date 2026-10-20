using ElevatorSystem.Contracts;

namespace ElevatorSystem.Web.Services;

/// <summary>The read side of the elevator REST API (<see cref="ApiRoutes"/>): the building and the car's status.</summary>
public interface IElevatorApiClient
{
    Task<ElevatorConfigurationDto> GetConfigurationAsync(CancellationToken cancellationToken = default);

    Task<ElevatorStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);
}
