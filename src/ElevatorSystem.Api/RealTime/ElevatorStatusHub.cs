using ElevatorSystem.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace ElevatorSystem.Api.RealTime;

/// <summary>
/// The real-time channel at <see cref="ElevatorHub.Path"/>. Anonymous and receive-only: it declares no
/// client-callable methods, so commands can only arrive through the REST API, where validation and
/// rate limits are enforced (api-backend skill). Messages are pushed by <see cref="ElevatorBroadcastService"/>.
/// </summary>
internal sealed class ElevatorStatusHub(ElevatorStateCache cache) : Hub<IElevatorClient>
{
    /// <summary>
    /// A new client gets the latest status and counts straight away instead of waiting for the next change.
    /// Before the engine reported anything there is nothing to send; the first update reaches it as a broadcast.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        if (cache.Status is { } status)
        {
            await Clients.Caller.StatusChanged(status);
        }

        if (cache.Occupancy is { } occupancy)
        {
            await Clients.Caller.OccupancyChanged(occupancy);
        }

        await base.OnConnectedAsync();
    }
}
