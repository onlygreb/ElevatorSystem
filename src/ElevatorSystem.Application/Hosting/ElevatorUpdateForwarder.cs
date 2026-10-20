using System.Threading.Channels;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Contracts;
using Microsoft.Extensions.Logging;
using Log = ElevatorSystem.Application.Hosting.ElevatorEngineLog;

namespace ElevatorSystem.Application.Hosting;

/// <summary>
/// Publishes the engine's updates to the broker from a background worker, so the control loop never
/// waits for it (ADR-0007, ADR-0015).
/// </summary>
/// <remarks>
/// <para>
/// Updates queue in a bounded buffer that drops its oldest entry when full; every update carries the
/// whole status, so the next one supersedes anything dropped. Drops are counted and logged at most once
/// per <see cref="DropLogInterval"/>.
/// </para>
/// <para>
/// An elevator update is published only once the snapshot it reports is saved, unless saving is failing.
/// A status a client has seen therefore survives a crash, and after a restart or failover the restored
/// version continues above every version already shown (ADR-0016).
/// </para>
/// <para>
/// When nothing was published for <see cref="ElevatorEngineOptions.StatusRefreshInterval"/>, the latest
/// updates are sent again with their original ids: a subscriber that already holds them drops the copies,
/// one that joined late or missed them during a broker outage catches up.
/// </para>
/// </remarks>
internal sealed class ElevatorUpdateForwarder
{
    public static readonly TimeSpan DropLogInterval = TimeSpan.FromSeconds(10);

    private readonly IElevatorUpdatePublisher _publisher;
    private readonly SnapshotPersister _persister;
    private readonly ElevatorEngineOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Channel<object> _queue;
    private readonly Lock _dropLogGate = new();
    private long _dropped;
    private long _droppedAtLastLog;
    private DateTimeOffset? _lastDropLog;
    private bool _isPublishing = true;
    private ElevatorUpdateV1? _latestElevatorUpdate;
    private OccupancyUpdateV1? _latestOccupancyUpdate;

    public ElevatorUpdateForwarder(
        IElevatorUpdatePublisher publisher, SnapshotPersister persister, ElevatorEngineOptions options, TimeProvider timeProvider, ILogger logger)
    {
        _publisher = publisher;
        _persister = persister;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _queue = Channel.CreateBounded<object>(
            new BoundedChannelOptions(options.UpdateBufferCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            _ => OnDropped());
    }

    /// <summary>How many updates were dropped because the broker could not keep up.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Queues an elevator update. Safe from any thread; never blocks.</summary>
    public void Enqueue(ElevatorUpdateV1 update) => _queue.Writer.TryWrite(update);

    /// <summary>Queues an occupancy update. Safe from any thread; never blocks.</summary>
    public void Enqueue(OccupancyUpdateV1 update) => _queue.Writer.TryWrite(update);

    /// <summary>Accepts no more updates; <see cref="RunAsync"/> returns once it has published what is queued.</summary>
    public void Complete() => _queue.Writer.TryComplete();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var reader = _queue.Reader;
        try
        {
            while (true)
            {
                bool more;
                using (var refresh = new CancellationTokenSource(_options.StatusRefreshInterval, _timeProvider))
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, refresh.Token))
                {
                    try
                    {
                        more = await reader.WaitToReadAsync(wait.Token);
                    }
                    catch (OperationCanceledException) when (refresh.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        await RefreshAsync(cancellationToken);
                        continue;
                    }
                }

                if (!more)
                {
                    return;
                }

                while (reader.TryRead(out var update))
                {
                    await PublishAsync(update, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task PublishAsync(object update, CancellationToken cancellationToken)
    {
        switch (update)
        {
            case ElevatorUpdateV1 elevatorUpdate:
                await _persister.WaitUntilSavedAsync(elevatorUpdate.Status.Version, cancellationToken);
                _latestElevatorUpdate = elevatorUpdate;
                await TryPublishAsync(elevatorUpdate, cancellationToken);
                break;
            case OccupancyUpdateV1 occupancyUpdate:
                _latestOccupancyUpdate = occupancyUpdate;
                await TryPublishAsync(occupancyUpdate, cancellationToken);
                break;
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (_latestElevatorUpdate is { } elevatorUpdate)
        {
            await TryPublishAsync(elevatorUpdate, cancellationToken);
        }

        if (_latestOccupancyUpdate is { } occupancyUpdate)
        {
            await TryPublishAsync(occupancyUpdate, cancellationToken);
        }
    }

    private async Task TryPublishAsync(object update, CancellationToken cancellationToken)
    {
        try
        {
            await (update switch
            {
                ElevatorUpdateV1 elevatorUpdate => _publisher.PublishAsync(elevatorUpdate, cancellationToken),
                OccupancyUpdateV1 occupancyUpdate => _publisher.PublishAsync(occupancyUpdate, cancellationToken),
                _ => Task.CompletedTask,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // The broker being away must never stop the engine; the refresh re-sends the latest state later.
        catch (Exception exception)
        {
            if (_isPublishing)
            {
                _isPublishing = false;
                Log.PublishFailed(_logger, exception);
            }

            return;
        }

        if (!_isPublishing)
        {
            _isPublishing = true;
            Log.PublishRecovered(_logger);
        }
    }

    private void OnDropped()
    {
        var total = Interlocked.Increment(ref _dropped);
        var now = _timeProvider.GetUtcNow();
        long droppedSinceLastLog;
        lock (_dropLogGate)
        {
            if (_lastDropLog is { } last && now - last < DropLogInterval)
            {
                return;
            }

            droppedSinceLastLog = total - _droppedAtLastLog;
            _droppedAtLastLog = total;
            _lastDropLog = now;
        }

        Log.UpdatesDropped(_logger, droppedSinceLastLog, total, _options.UpdateBufferCapacity);
    }
}
