using System.Collections.Concurrent;
using ElevatorSystem.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace ElevatorSystem.Persistence.Tests;

[Collection(PostgresTestGroup.Name)]
public sealed class PostgresProcessedCommandStoreTests(PostgresFixture postgres)
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private readonly FakeTimeProvider _clock = new();

    [Fact]
    public async Task TryMarkProcessedAsync_SameIdTwice_ReturnsTrueThenFalse()
    {
        await using var database = await postgres.CreateMigratedDatabaseAsync();
        var store = Store(database);
        var commandId = Guid.CreateVersion7();

        var first = await store.TryMarkProcessedAsync(commandId, TestContext.Current.CancellationToken);
        var second = await store.TryMarkProcessedAsync(commandId, TestContext.Current.CancellationToken);

        first.ShouldBeTrue();
        second.ShouldBeFalse("a redelivered command must not be applied again");
    }

    [Fact]
    public async Task TryMarkProcessedAsync_ConcurrentDuplicates_ReturnsTrueExactlyOncePerId()
    {
        await using var database = await postgres.CreateMigratedDatabaseAsync();
        var store = Store(database);
        var ids = Enumerable.Range(0, 40).Select(_ => Guid.CreateVersion7()).ToArray();
        var attempts = ids.SelectMany(id => Enumerable.Repeat(id, 10)).ToArray();
        Random.Shared.Shuffle(attempts);
        var wins = new ConcurrentBag<Guid>();

        await Parallel.ForEachAsync(
            attempts,
            new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = TestContext.Current.CancellationToken },
            async (id, cancellationToken) =>
            {
                if (await store.TryMarkProcessedAsync(id, cancellationToken))
                {
                    wins.Add(id);
                }
            });

        wins.Order().ShouldBe(ids.Order(), "each command is applied exactly once, and none is lost");
    }

    [Fact]
    public async Task PurgeExpiredAsync_RemovesOnlyEntriesOlderThanTheRetention()
    {
        await using var database = await postgres.CreateMigratedDatabaseAsync();
        var store = Store(database);
        var expired = Guid.CreateVersion7();
        var atTheCutoff = Guid.CreateVersion7();
        var recent = Guid.CreateVersion7();
        await store.TryMarkProcessedAsync(expired, TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await store.TryMarkProcessedAsync(atTheCutoff, TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromDays(3));
        await store.TryMarkProcessedAsync(recent, TestContext.Current.CancellationToken);
        _clock.Advance(Retention - TimeSpan.FromDays(3));

        var purged = await store.PurgeExpiredAsync(TestContext.Current.CancellationToken);

        purged.ShouldBe(1);
        (await store.TryMarkProcessedAsync(recent, TestContext.Current.CancellationToken)).ShouldBeFalse("a recent entry is still remembered");
        (await store.TryMarkProcessedAsync(atTheCutoff, TestContext.Current.CancellationToken)).ShouldBeFalse("an entry exactly as old as the retention is kept");
        (await store.TryMarkProcessedAsync(expired, TestContext.Current.CancellationToken)).ShouldBeTrue("the expired entry was forgotten");
    }

    [Fact]
    public async Task PurgeExpiredAsync_MoreThanOneBatch_RemovesEveryExpiredEntryAndIsIdempotent()
    {
        await using var database = await postgres.CreateMigratedDatabaseAsync();
        var store = Store(database);
        var expiredCount = PostgresProcessedCommandStore.PurgeBatchSize * 2 + 37;
        await Parallel.ForEachAsync(
            Enumerable.Range(0, expiredCount),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = TestContext.Current.CancellationToken },
            async (_, cancellationToken) => await store.TryMarkProcessedAsync(Guid.CreateVersion7(), cancellationToken));
        _clock.Advance(Retention + TimeSpan.FromMinutes(1));
        var recent = Guid.CreateVersion7();
        await store.TryMarkProcessedAsync(recent, TestContext.Current.CancellationToken);

        var purged = await store.PurgeExpiredAsync(TestContext.Current.CancellationToken);
        var purgedAgain = await store.PurgeExpiredAsync(TestContext.Current.CancellationToken);

        purged.ShouldBe(expiredCount);
        purgedAgain.ShouldBe(0);
        (await store.TryMarkProcessedAsync(recent, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    private PostgresProcessedCommandStore Store(TestDatabase database) =>
        new(database.ContextFactory, Options.Create(new PersistenceOptions { ProcessedCommandRetention = Retention }), _clock);
}
