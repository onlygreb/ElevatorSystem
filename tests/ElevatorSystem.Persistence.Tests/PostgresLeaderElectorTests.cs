using ElevatorSystem.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ElevatorSystem.Persistence.Tests;

/// <summary>
/// Leader election against a real server, in real time with short intervals. Waits are bounded by
/// <see cref="Patience"/>; nothing sleeps for a fixed time except where a test proves that something
/// does not happen.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PostgresLeaderElectorTests(PostgresFixture postgres)
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private readonly long _lockId = Random.Shared.NextInt64(1, long.MaxValue);

    [Fact]
    public async Task AcquireAsync_WhileAnotherLeads_WaitsUntilTheLeaderDisposes()
    {
        await using var database = await postgres.CreateMigratedDatabaseAsync();
        var first = await Elector(database).AcquireAsync(TestContext.Current.CancellationToken);

        var second = Elector(database).AcquireAsync(TestContext.Current.CancellationToken);
        await Should.ThrowAsync<TimeoutException>(
            second.WaitAsync(CheckInterval * 6, TestContext.Current.CancellationToken),
            "a standby must not lead while the leader holds the lock");
        await first.DisposeAsync();

        await using var lease = await second.WaitAsync(Patience, TestContext.Current.CancellationToken);
        first.Lost.IsCancellationRequested.ShouldBeTrue("a disposed lease no longer leads");
        lease.Lost.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task AcquireAsync_ManyContenders_NeverTwoLeadersAtOnce()
    {
        await using var database = await postgres.CreateMigratedDatabaseAsync();
        var leaders = 0;
        var overlaps = 0;
        var terms = 0;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, 4),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = TestContext.Current.CancellationToken },
            async (_, cancellationToken) =>
            {
                var elector = Elector(database);
                for (var term = 0; term < 2; term++)
                {
                    await using var lease = await elector.AcquireAsync(cancellationToken).WaitAsync(Patience * 2, cancellationToken);
                    if (Interlocked.Increment(ref leaders) > 1)
                    {
                        Interlocked.Increment(ref overlaps);
                    }

                    await Task.Delay(CheckInterval, cancellationToken);
                    Interlocked.Decrement(ref leaders);
                    Interlocked.Increment(ref terms);
                }
            });

        terms.ShouldBe(8, "every contender led twice");
        overlaps.ShouldBe(0, "two engines must never lead at the same time");
    }

    [Fact]
    public async Task Lease_LeaderConnectionKilled_CancelsLostBeforeTheStandbyTakesOver()
    {
        await using var database = await postgres.CreateMigratedDatabaseAsync();
        await using var leader = await Elector(database).AcquireAsync(TestContext.Current.CancellationToken);
        var standby = Elector(database).AcquireAsync(TestContext.Current.CancellationToken);
        var leaderLostWhenStandbyLed = standby.ContinueWith(
            _ => leader.Lost.IsCancellationRequested,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        (await TerminateLockHolderAsync(database)).ShouldBe(1, "exactly one session holds the leader lock");

        await WaitUntilCancelledAsync(leader.Lost);
        await using var newLeader = await standby.WaitAsync(Patience, TestContext.Current.CancellationToken);
        (await leaderLostWhenStandbyLed).ShouldBeTrue("the old leader must have stopped before the standby starts");
        newLeader.Lost.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task DisposeAsync_ReleasesTheLockAtOnce()
    {
        await using var database = await postgres.CreateMigratedDatabaseAsync();
        var lease = await Elector(database).AcquireAsync(TestContext.Current.CancellationToken);
        (await CountLockHoldersAsync(database)).ShouldBe(1);

        await lease.DisposeAsync();

        lease.Lost.IsCancellationRequested.ShouldBeTrue();
        (await CountLockHoldersAsync(database)).ShouldBe(0);
    }

    [Fact]
    public async Task AcquireAsync_CancelledWhileStandingBy_ThrowsAndLeavesNoLock()
    {
        await using var database = await postgres.CreateMigratedDatabaseAsync();
        var leader = await Elector(database).AcquireAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var standby = Elector(database).AcquireAsync(cancellation.Token);

        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(standby.WaitAsync(Patience, TestContext.Current.CancellationToken));
        await leader.DisposeAsync();
        (await CountLockHoldersAsync(database)).ShouldBe(0, "the cancelled standby must not keep a lock");
    }

    [Fact]
    public async Task AcquireAsync_DatabaseUnreachable_KeepsRetryingUntilCancelled()
    {
        var unreachable = new NpgsqlConnectionStringBuilder(postgres.AdminConnectionString) { Port = 1, Timeout = 1 }.ConnectionString;
        await using var database = new ElevatorDatabase(unreachable);
        using var cancellation = new CancellationTokenSource();

        var acquiring = Elector(database).AcquireAsync(cancellation.Token);
        await Should.ThrowAsync<TimeoutException>(acquiring.WaitAsync(RetryInterval * 10, TestContext.Current.CancellationToken));
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(acquiring.WaitAsync(Patience, TestContext.Current.CancellationToken));
    }

    private PostgresLeaderElector Elector(TestDatabase database) => Elector(database.Database);

    private PostgresLeaderElector Elector(ElevatorDatabase database) =>
        new(
            database,
            Options.Create(new PersistenceOptions
            {
                LeaderLockId = _lockId,
                LeaderCheckInterval = CheckInterval,
                LeaderAcquireRetryInterval = RetryInterval,
            }),
            TimeProvider.System);

    private Task<long> CountLockHoldersAsync(TestDatabase database) =>
        QueryLockHoldersAsync(database, "SELECT count(*)");

    private Task<long> TerminateLockHolderAsync(TestDatabase database) =>
        QueryLockHoldersAsync(database, "SELECT count(*) FILTER (WHERE pg_terminate_backend(pid))");

    // A bigint advisory key is stored in pg_locks as classid (high 32 bits) and objid (low 32 bits).
    private async Task<long> QueryLockHoldersAsync(TestDatabase database, string select)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            select + """
                 FROM pg_locks
                WHERE locktype = 'advisory' AND granted AND objsubid = 1
                  AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                  AND ((classid::bigint << 32) | objid::bigint) = $1
            """,
            connection)
        {
            Parameters = { new NpgsqlParameter { Value = _lockId } },
        };

        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task WaitUntilCancelledAsync(CancellationToken token)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = token.Register(() => cancelled.TrySetResult());
        await cancelled.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
    }
}
