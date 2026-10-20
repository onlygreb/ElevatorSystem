using System.Net.Http.Json;
using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Resources;

namespace ElevatorSystem.Web.Services;

/// <summary>
/// Typed client for the elevator REST API on the page's own origin, serialising with <see cref="ContractsJsonContext"/>.
/// Passengers move through <see cref="IPassengerApiClient"/>; this reads the building and the car.
/// </summary>
internal sealed class ElevatorApiClient(HttpClient http) : IElevatorApiClient
{
    public async Task<ElevatorConfigurationDto> GetConfigurationAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync(ApiRoutes.Configuration, ContractsJsonContext.Default.ElevatorConfigurationDto, cancellationToken)
        ?? throw new InvalidOperationException("The API returned an empty configuration.");

    public async Task<ElevatorStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync(ApiRoutes.Elevator, ContractsJsonContext.Default.ElevatorStatusDto, cancellationToken)
        ?? throw new InvalidOperationException("The API returned an empty status.");

    /// <summary>The <see cref="Text"/> key of the passenger-facing explanation for <paramref name="reason"/>.</summary>
    internal static string MessageKeyFor(RejectionReason reason) => reason switch
    {
        RejectionReason.InvalidFloor => Text.RefusedInvalidFloor,
        RejectionReason.InvalidDirection => Text.RefusedInvalidDirection,
        RejectionReason.OutOfService => Text.RefusedOutOfService,
        RejectionReason.Busy => Text.RefusedBusy,
        _ => Text.RefusedUnexpected,
    };
}
