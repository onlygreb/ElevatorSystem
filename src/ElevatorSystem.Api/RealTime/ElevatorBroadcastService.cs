using ElevatorSystem.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace ElevatorSystem.Api.RealTime;

/// <summary>
/// Drains <see cref="ElevatorEventBuffer"/> and fans each update out to every hub client, away from the
/// subscription's thread (ADR-0015).
/// </summary>
/// <remarks>
/// Every elevator update becomes an <see cref="IElevatorClient.ActivityOccurred"/> message, in order. After
/// each batch (at most <see cref="MaxBatchSize"/> updates) clients get one <see cref="IElevatorClient.StatusChanged"/>
/// with the newest status and one <see cref="IElevatorClient.OccupancyChanged"/> with the newest counts (to staff only), each only
/// if it changed since the last one sent, so a burst of hundreds of requests produces a handful of status messages
/// rather than hundreds of identical ones. A failing send is logged and skipped; it never stops the broadcast. A
/// send that never completes is abandoned on shutdown, so a stuck client cannot hold the host open.
/// </remarks>
internal sealed class ElevatorBroadcastService(
    ElevatorEventBuffer buffer,
    IHubContext<ElevatorStatusHub, IElevatorClient> hub,
    ILogger<ElevatorBroadcastService> logger) : BackgroundService
{
    public const int MaxBatchSize = 64;

    private ElevatorStatusDto? _lastStatusSent;
    private BuildingOccupancyDto? _lastOccupancySent;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = buffer.Reader;
        try
        {
            while (await reader.WaitToReadAsync(stoppingToken))
            {
                ElevatorStatusDto? status = null;
                BuildingOccupancyDto? occupancy = null;
                for (var taken = 0; taken < MaxBatchSize && reader.TryRead(out var update); taken++)
                {
                    switch (update)
                    {
                        case ElevatorUpdateV1 elevatorUpdate:
                            await SendAsync(clients => clients.All, client => client.ActivityOccurred(elevatorUpdate.Activity), nameof(IElevatorClient.ActivityOccurred), stoppingToken);
                            status = elevatorUpdate.Status;
                            break;
                        case OccupancyUpdateV1 occupancyUpdate:
                            occupancy = occupancyUpdate.Occupancy;
                            break;
                    }
                }

                if (status is not null && !SameStatus(status, _lastStatusSent))
                {
                    _lastStatusSent = status;
                    await SendAsync(clients => clients.All, client => client.StatusChanged(status), nameof(IElevatorClient.StatusChanged), stoppingToken);
                }

                if (occupancy is not null && !ReferenceEquals(occupancy, _lastOccupancySent))
                {
                    _lastOccupancySent = occupancy;
                    await SendAsync(clients => clients.Group(ElevatorStatusHub.StaffGroup), client => client.OccupancyChanged(occupancy), nameof(IElevatorClient.OccupancyChanged), stoppingToken);
                }
            }
        }
        // Shutdown: an abandoned or failing send no longer matters.
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    // One status per version: the updates of one control-loop step, and refused requests, share a version.
    private static bool SameStatus(ElevatorStatusDto status, ElevatorStatusDto? last) =>
        last is not null && last.Version == status.Version;

    private async Task SendAsync(
        Func<IHubClients<IElevatorClient>, IElevatorClient> audience, Func<IElevatorClient, Task> send, string message, CancellationToken stoppingToken)
    {
        try
        {
            // Choosing the audience is inside the try too: a broken hub context must not end the broadcast either.
            await send(audience(hub.Clients)).WaitAsync(stoppingToken);
        }
        // One failed message must not end the feed for everyone (ADR-0015).
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            ApiLog.BroadcastFailed(logger, exception, message);
        }
    }
}
