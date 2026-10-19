using ElevatorSystem.Application;
using ElevatorSystem.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace ElevatorSystem.Api.RealTime;

/// <summary>
/// The real-time channel at <see cref="ElevatorHub.Path"/>. Anonymous and receive-only: it declares no
/// client-callable methods, so commands can only arrive through the REST API, where validation and
/// rate limits are enforced (api-backend skill). Messages are pushed by <see cref="ElevatorBroadcastService"/>.
/// </summary>
internal sealed class ElevatorStatusHub(ElevatorController controller) : Hub<IElevatorClient>
{
    /// <summary>A new client gets the current status straight away instead of waiting for the next change.</summary>
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.StatusChanged(ElevatorContractMapper.ToStatus(controller.Elevator.Snapshot()));
        await base.OnConnectedAsync();
    }
}
