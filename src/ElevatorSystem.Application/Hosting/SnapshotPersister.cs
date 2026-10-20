using System.Threading.Channels;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Domain;
using Microsoft.Extensions.Logging;
using Log = ElevatorSystem.Application.Hosting.ElevatorEngineLog;

namespace ElevatorSystem.Application.Hosting;

/// <summary>
/// Saves the elevator's latest snapshot in the background, away from the control loop (ADR-0007).
/// </summary>
/// <remarks>
/// <see cref="Notify"/> never blocks: a burst of changes collapses into one pending signal, and each save
/// takes the snapshot current at that moment, so only the newest state is ever written. A failed save is
/// retried every <see cref="ElevatorEngineOptions.StoreRetryInterval"/> until it succeeds; meanwhile
/// <see cref="IsAvailable"/> is <see langword="false"/> and nobody waits for it.
/// </remarks>
internal sealed class SnapshotPersister
{
    private readonly Elevator _elevator;
    private readonly IElevatorStateStore _store;
    private readonly ElevatorEngineOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });

    private long _savedVersion;
    private volatile bool _isAvailable = true;
    private volatile bool _isStopped;
    private TaskCompletionSource _progress = NewSignal();

    // savedVersion: the version already in the store (the restored snapshot's), or -1 when nothing is stored.
    public SnapshotPersister(
        Elevator elevator, IElevatorStateStore store, long savedVersion, ElevatorEngineOptions options, TimeProvider timeProvider, ILogger logger)
    {
        _elevator = elevator;
        _store = store;
        _savedVersion = savedVersion;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>The highest version known to be stored.</summary>
    public long SavedVersion => Interlocked.Read(ref _savedVersion);

    /// <summary>Whether the last save succeeded. While it is <see langword="false"/>, waiting for a save returns at once.</summary>
    public bool IsAvailable => _isAvailable;

    /// <summary>Asks for the current state to be saved. Safe from any thread, including the control loop; never blocks.</summary>
    public void Notify() => _signal.Writer.TryWrite(true);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _signal.Reader.WaitToReadAsync(cancellationToken))
            {
                _signal.Reader.TryRead(out _);
                await SaveLatestAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Nobody may wait for a save that will never come.
            _isStopped = true;
            Pulse();
        }
    }

    /// <summary>
    /// Completes once <paramref name="version"/> (or a newer one) is stored, the store is unavailable, the
    /// persister has stopped, or <see cref="ElevatorEngineOptions.StoreTimeout"/> has passed, whichever comes first.
    /// </summary>
    public async Task WaitUntilSavedAsync(long version, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_options.StoreTimeout, _timeProvider);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        while (true)
        {
            var progress = Volatile.Read(ref _progress);
            if (SavedVersion >= version || !_isAvailable || _isStopped)
            {
                return;
            }

            try
            {
                await progress.Task.WaitAsync(wait.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>Saves the current state and waits for it, within the same bounds as <see cref="WaitUntilSavedAsync"/>.</summary>
    public Task FlushAsync(CancellationToken cancellationToken)
    {
        var version = _elevator.Snapshot().Version;
        Notify();
        return WaitUntilSavedAsync(version, cancellationToken);
    }

    private async Task SaveLatestAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var snapshot = _elevator.Snapshot();
            if (snapshot.Version <= SavedVersion)
            {
                return;
            }

            try
            {
                using var timeout = new CancellationTokenSource(_options.StoreTimeout, _timeProvider);
                using var save = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                await _store.SaveAsync(snapshot, save.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // A database outage must never reach the car: report it, keep the newest state, retry (ADR-0007).
            catch (Exception exception)
            {
                if (_isAvailable)
                {
                    _isAvailable = false;
                    Log.SaveFailed(_logger, exception, _options.StoreRetryInterval);
                    Pulse();
                }

                await Task.Delay(_options.StoreRetryInterval, _timeProvider, cancellationToken);
                continue;
            }

            Interlocked.Exchange(ref _savedVersion, snapshot.Version);
            if (!_isAvailable)
            {
                _isAvailable = true;
                Log.SaveRecovered(_logger, snapshot.Version);
            }

            Pulse();
            return;
        }
    }

    private void Pulse() => Interlocked.Exchange(ref _progress, NewSignal()).TrySetResult();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
