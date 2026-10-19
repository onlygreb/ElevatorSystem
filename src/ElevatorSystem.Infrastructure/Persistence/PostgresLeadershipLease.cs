using ElevatorSystem.Application.Ports;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ElevatorSystem.Infrastructure.Persistence;

/// <summary>
/// Leadership held through an advisory lock on a dedicated connection. A monitor checks the
/// connection on every interval; the first failed or timed-out check cancels <see cref="Lost"/> and
/// closes the connection, which ends the session and frees the lock for a standby.
/// </summary>
internal sealed class PostgresLeadershipLease : ILeadershipLease
{
    // Never disposed: it has no timer, and Lost must stay readable after the lease is gone.
    private readonly CancellationTokenSource _lost = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly NpgsqlConnection _connection;
    private readonly long _lockId;
    private readonly TimeSpan _checkInterval;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Task _monitor;
    private int _disposed;

    public PostgresLeadershipLease(NpgsqlConnection connection, long lockId, TimeSpan checkInterval, TimeProvider timeProvider, ILogger logger)
    {
        _connection = connection;
        _lockId = lockId;
        _checkInterval = checkInterval;
        _timeProvider = timeProvider;
        _logger = logger;
        Lost = _lost.Token;
        _monitor = MonitorAsync();
    }

    public CancellationToken Lost { get; }

    /// <summary>
    /// Waits <paramref name="delay"/> while the monitor keeps checking, and returns whether leadership
    /// is still held at the end.
    /// </summary>
    public async Task<bool> ConfirmAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        using var waitOrLost = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Lost);
        try
        {
            await Task.Delay(delay, _timeProvider, waitOrLost.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Lost while waiting.
        }

        return !Lost.IsCancellationRequested;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // Stop the monitor between checks (an in-flight check finishes within its timeout), so the
        // unlock below never races a check on the same connection.
        await _stopping.CancelAsync();
        await _monitor;

        if (!_lost.IsCancellationRequested)
        {
            // Tell the holder first: it must stop before anyone else can take the lock.
            await _lost.CancelAsync();
            try
            {
                using var timeout = new CancellationTokenSource(_checkInterval, _timeProvider);
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", _connection)
                {
                    Parameters = { new NpgsqlParameter { Value = _lockId } },
                };
                await command.ExecuteScalarAsync(timeout.Token);
                PersistenceLog.LeadershipReleased(_logger, _lockId);
            }
            catch (Exception exception)
            {
                PersistenceLog.LeadershipReleaseFailed(_logger, exception, _lockId);
            }
        }

        await _connection.DisposeAsync();
        _stopping.Dispose();
    }

    private async Task MonitorAsync()
    {
        // Run the loop off the caller's stack, so constructing the lease never blocks.
        await Task.Yield();

        using var timer = new PeriodicTimer(_checkInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token))
            {
                using var timeout = new CancellationTokenSource(_checkInterval, _timeProvider);
                await using var command = new NpgsqlCommand("SELECT 1", _connection);
                await command.ExecuteScalarAsync(timeout.Token);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            PersistenceLog.LeadershipLost(_logger, exception, _lockId);
        }

        // Leadership is lost: tell the holder at once, then drop the connection so the server ends the
        // session (if it has not already) and the lock is freed.
        await _lost.CancelAsync();
        await _connection.CloseAsync();
    }
}
