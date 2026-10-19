using System.Globalization;
using ElevatorSystem.Application.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ElevatorSystem.Infrastructure.Persistence;

/// <summary>
/// Elects the single active engine with a PostgreSQL session-level advisory lock (ADR-0007, ADR-0019).
/// The lock lives as long as a dedicated, unpooled connection: the lease checks that connection on
/// every <see cref="PersistenceOptions.LeaderCheckInterval"/> and cancels
/// <see cref="ILeadershipLease.Lost"/> the moment a check fails. A new leader waits
/// <see cref="TakeoverDelay"/> after taking the lock before it starts, which is longer than a
/// previous leader needs to notice that it lost the lock, so two leaders never act at once.
/// </summary>
public sealed class PostgresLeaderElector(
    ElevatorDatabase database,
    IOptions<PersistenceOptions> options,
    TimeProvider timeProvider,
    ILogger<PostgresLeaderElector>? logger = null) : ILeaderElector
{
    private readonly ElevatorDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly PersistenceOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger _logger = logger ?? NullLogger<PostgresLeaderElector>.Instance;

    /// <summary>
    /// How long a new leader waits after taking the lock. A previous leader notices a lost lock within
    /// two check intervals (the wait for its next check, then that check's timeout); one more is margin.
    /// </summary>
    public TimeSpan TakeoverDelay => _options.LeaderCheckInterval * 3;

    /// <summary>
    /// PostgreSQL ends the leader's session when it sends nothing for this long, so the lock is freed
    /// even when the leader's host vanishes without closing its connection.
    /// </summary>
    internal TimeSpan SessionIdleTimeout => _options.LeaderCheckInterval * 5;

    public async Task<ILeadershipLease> AcquireAsync(CancellationToken cancellationToken)
    {
        var reportedStandby = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NpgsqlConnection? connection = null;
            try
            {
                connection = _database.CreateLeaseConnection();
                await connection.OpenAsync(cancellationToken);

                while (!await TryLockAsync(connection, cancellationToken))
                {
                    if (!reportedStandby)
                    {
                        PersistenceLog.StandingBy(_logger, _options.LeaderLockId);
                        reportedStandby = true;
                    }

                    await Task.Delay(_options.LeaderAcquireRetryInterval, _timeProvider, cancellationToken);
                }

                await SetSessionIdleTimeoutAsync(connection, cancellationToken);
                var lease = new PostgresLeadershipLease(connection, _options.LeaderLockId, _options.LeaderCheckInterval, _timeProvider, _logger);
                connection = null;

                bool confirmed;
                try
                {
                    confirmed = await lease.ConfirmAsync(TakeoverDelay, cancellationToken);
                }
                catch
                {
                    await lease.DisposeAsync();
                    throw;
                }

                if (confirmed)
                {
                    PersistenceLog.LeadershipAcquired(_logger, _options.LeaderLockId);
                    return lease;
                }

                // Lost the lock while waiting to take over; compete again.
                await lease.DisposeAsync();
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // A database error that races with cancellation still ends as a cancellation.
                cancellationToken.ThrowIfCancellationRequested();
                PersistenceLog.LeadershipAttemptFailed(_logger, exception, _options.LeaderLockId, _options.LeaderAcquireRetryInterval);
            }
            finally
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync();
                }
            }

            await Task.Delay(_options.LeaderAcquireRetryInterval, _timeProvider, cancellationToken);
        }
    }

    private async Task<bool> TryLockAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock($1)", connection)
        {
            Parameters = { new NpgsqlParameter { Value = _options.LeaderLockId } },
        };

        var locked = await command.ExecuteScalarAsync(cancellationToken) is true;
        if (locked)
        {
            PersistenceLog.LeaderLockTaken(_logger, _options.LeaderLockId, TakeoverDelay);
        }

        return locked;
    }

    private async Task SetSessionIdleTimeoutAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('idle_session_timeout', $1, false)", connection)
        {
            Parameters =
            {
                new NpgsqlParameter { Value = ((long)SessionIdleTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) },
            },
        };

        await command.ExecuteScalarAsync(cancellationToken);
    }
}
