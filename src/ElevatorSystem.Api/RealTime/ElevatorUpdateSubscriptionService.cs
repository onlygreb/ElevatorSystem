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
    private readonly CancellationTokenSource _stopping = new();
    private Task? _subscription;
    private bool _disposed;

    /// <summary>
    /// Subscribes before returning. Since .NET 10 a <see cref="BackgroundService"/> runs all of
    /// <see cref="ExecuteAsync"/> on the thread pool, and the in-memory bus delivers only to subscribers already
    /// registered, so an in-process engine starting next could otherwise publish its opening status before anyone
    /// listened, leaving the API without a status until the next refresh. Hosts call <c>StartAsync</c> in
    /// registration order, and the real-time services are registered before the engine.
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = subscriber.SubscribeAsync(OnElevatorUpdate, OnOccupancyUpdate, _stopping.Token);
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // A host may stop a service it has already disposed; the subscription ended with it then.
        if (!_disposed)
        {
            await _stopping.CancelAsync();
        }

        // Wait for the subscription itself: the base class may not have started ExecuteAsync yet on a busy thread pool.
        if (_subscription is { } subscription)
        {
            await subscription.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        if (!_disposed)
        {
            _stopping.Cancel();
            _stopping.Dispose();
            _disposed = true;
        }

        base.Dispose();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _subscription ?? Task.CompletedTask;

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
