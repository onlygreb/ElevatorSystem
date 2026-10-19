using ElevatorSystem.Application;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain.Events;
using Microsoft.AspNetCore.SignalR;

namespace ElevatorSystem.Api.RealTime;

/// <summary>
/// Drains <see cref="ElevatorEventBuffer"/> and fans each event out to every hub client, away from the
/// control loop's thread.
/// </summary>
/// <remarks>
/// Every event becomes an <see cref="IElevatorClient.ActivityOccurred"/> message, in order. After each
/// batch (at most <see cref="MaxBatchSize"/> events) clients get one <see cref="IElevatorClient.StatusChanged"/>
/// with the current snapshot, and only if its version changed since the last one sent, so a burst of
/// hundreds of requests produces a handful of status messages rather than hundreds of identical ones.
/// A failing send is logged and skipped; it never stops the broadcast or reaches the elevator. A send
/// that never completes is abandoned on shutdown, so a stuck client cannot hold the host open.
/// </remarks>
internal sealed class ElevatorBroadcastService(
    ElevatorEventBuffer buffer,
    ElevatorController controller,
    IHubContext<ElevatorStatusHub, IElevatorClient> hub,
    ILogger<ElevatorBroadcastService> logger) : BackgroundService
{
    public const int MaxBatchSize = 64;

    private long _lastStatusVersion = -1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = buffer.Reader;
        try
        {
            while (await reader.WaitToReadAsync(stoppingToken))
            {
                for (var sent = 0; sent < MaxBatchSize && reader.TryRead(out var elevatorEvent); sent++)
                {
                    await SendActivityAsync(elevatorEvent, stoppingToken);
                }

                await SendStatusIfChangedAsync(stoppingToken);
            }
        }
        // Shutdown: an abandoned or failing send no longer matters.
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task SendActivityAsync(ElevatorEvent elevatorEvent, CancellationToken stoppingToken)
    {
        try
        {
            await hub.Clients.All.ActivityOccurred(ElevatorContractMapper.ToActivity(elevatorEvent)).WaitAsync(stoppingToken);
        }
        // One failed message must not end the feed for everyone (ADR-0015).
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            ApiLog.BroadcastFailed(logger, exception, nameof(IElevatorClient.ActivityOccurred));
        }
    }

    private async Task SendStatusIfChangedAsync(CancellationToken stoppingToken)
    {
        var snapshot = controller.Elevator.Snapshot();
        if (snapshot.Version == _lastStatusVersion)
        {
            return;
        }

        _lastStatusVersion = snapshot.Version;
        try
        {
            await hub.Clients.All.StatusChanged(ElevatorContractMapper.ToStatus(snapshot)).WaitAsync(stoppingToken);
        }
        // Clients resynchronise from the next status; the broadcast goes on.
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            ApiLog.BroadcastFailed(logger, exception, nameof(IElevatorClient.StatusChanged));
        }
    }
}
