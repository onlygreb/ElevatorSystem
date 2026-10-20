using ElevatorSystem.Application.Ports;
using ElevatorSystem.Contracts;

namespace ElevatorSystem.Api.RealTime;

/// <summary>
/// Receives the engine's updates for the lifetime of the host: each new one goes into the
/// <see cref="ElevatorStateCache"/> and, for the hub, into the <see cref="ElevatorEventBuffer"/>. Copies the
/// cache already holds go no further. The subscriber waits out broker outages by itself (ADR-0018), so the
/// cache simply keeps the last known state meanwhile.
/// </summary>
internal sealed class ElevatorUpdateSubscriptionService(
    IElevatorUpdateSubscriber subscriber, ElevatorStateCache cache, ElevatorEventBuffer buffer) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        subscriber.SubscribeAsync(OnElevatorUpdate, OnOccupancyUpdate, stoppingToken);

    private Task OnElevatorUpdate(ElevatorUpdateV1 update, CancellationToken cancellationToken)
    {
        if (cache.TryApply(update))
        {
            buffer.Publish(update);
        }

        return Task.CompletedTask;
    }

    private Task OnOccupancyUpdate(OccupancyUpdateV1 update, CancellationToken cancellationToken)
    {
        if (cache.TryApply(update))
        {
            buffer.Publish(update);
        }

        return Task.CompletedTask;
    }
}
