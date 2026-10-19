using ElevatorSystem.Application.InMemory.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace ElevatorSystem.Application.Tests.InMemory.Persistence;

public sealed class InMemoryProcessedCommandStoreTests
{
    [Fact]
    public async Task TryMarkProcessedAsync_SameIdTwice_ReturnsTrueThenFalse()
    {
        var store = new InMemoryProcessedCommandStore(new FakeTimeProvider());
        var commandId = Guid.NewGuid();

        var first = await store.TryMarkProcessedAsync(commandId, TestContext.Current.CancellationToken);
        var second = await store.TryMarkProcessedAsync(commandId, TestContext.Current.CancellationToken);

        first.ShouldBeTrue();
        second.ShouldBeFalse("a redelivered command must not be applied again");
    }

    [Fact]
    public async Task TryMarkProcessedAsync_ConcurrentDuplicates_ReturnsTrueExactlyOncePerId()
    {
        var store = new InMemoryProcessedCommandStore(new FakeTimeProvider());
        var ids = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToArray();
        var attempts = ids.SelectMany(id => Enumerable.Repeat(id, 20)).ToArray();
        Random.Shared.Shuffle(attempts);
        var wins = new System.Collections.Concurrent.ConcurrentBag<Guid>();

        await Parallel.ForEachAsync(attempts, TestContext.Current.CancellationToken, async (id, cancellationToken) =>
        {
            if (await store.TryMarkProcessedAsync(id, cancellationToken))
            {
                wins.Add(id);
            }
        });

        wins.Order().ShouldBe(ids.Order(), "each command is applied exactly once");
    }

    [Fact]
    public async Task PurgeExpired_RemovesOnlyEntriesOlderThanTheRetention()
    {
        var clock = new FakeTimeProvider();
        var store = new InMemoryProcessedCommandStore(clock);
        var expired = Guid.NewGuid();
        var kept = Guid.NewGuid();
        await store.TryMarkProcessedAsync(expired, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromDays(2));
        await store.TryMarkProcessedAsync(kept, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromDays(5) + TimeSpan.FromSeconds(1));

        var removed = store.PurgeExpired(TimeSpan.FromDays(7));

        removed.ShouldBe(1);
        (await store.TryMarkProcessedAsync(kept, TestContext.Current.CancellationToken)).ShouldBeFalse("the recent entry is still remembered");
        (await store.TryMarkProcessedAsync(expired, TestContext.Current.CancellationToken)).ShouldBeTrue("the expired entry was forgotten");
    }
}
