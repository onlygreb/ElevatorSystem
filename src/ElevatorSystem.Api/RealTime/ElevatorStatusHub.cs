using ElevatorSystem.Api.Staff;
using ElevatorSystem.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ElevatorSystem.Api.RealTime;

/// <summary>
/// The real-time channel at <see cref="ElevatorHub.Path"/>. Anonymous and receive-only: it declares no
/// client-callable methods, so commands can only arrive through the REST API, where validation and
/// rate limits are enforced (api-backend skill). Messages are pushed by <see cref="ElevatorBroadcastService"/>.
/// </summary>
/// <remarks>
/// Everyone gets the status and the activity feed. Occupancy goes only to the <see cref="StaffGroup"/>: connections
/// whose session meets <see cref="StaffPolicies.Staff"/> when they connect (ADR-0022). A staff connection is closed
/// when its session cookie expires, so it does not outlive the sign-in.
/// </remarks>
internal sealed class ElevatorStatusHub(ElevatorStateCache cache, IAuthorizationService authorization) : Hub<IElevatorClient>
{
    public const string StaffGroup = "staff";

    /// <summary>
    /// A new client gets the latest status (and, for staff, the counts) straight away instead of waiting for the next
    /// change. Before the engine reported anything there is nothing to send; the first update reaches it as a broadcast.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        if (cache.Status is { } status)
        {
            await Clients.Caller.StatusChanged(status);
        }

        if (Context.User is { } user && (await authorization.AuthorizeAsync(user, StaffPolicies.Staff)).Succeeded)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, StaffGroup);
            if (cache.Occupancy is { } occupancy)
            {
                await Clients.Caller.OccupancyChanged(occupancy);
            }
        }

        await base.OnConnectedAsync();
    }
}
