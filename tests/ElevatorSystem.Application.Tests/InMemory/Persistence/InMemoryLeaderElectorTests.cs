using ElevatorSystem.Application.InMemory.Persistence;

namespace ElevatorSystem.Application.Tests.InMemory.Persistence;

public sealed class InMemoryLeaderElectorTests
{
    [Fact]
    public async Task AcquireAsync_WhileAnotherLeads_WaitsUntilTheLeaseIsDisposed()
    {
        using var elector = new InMemoryLeaderElector();
        var first = await elector.AcquireAsync(TestContext.Current.CancellationToken);

        var second = elector.AcquireAsync(TestContext.Current.CancellationToken);
        second.IsCompleted.ShouldBeFalse("only one caller may lead at a time");
        await first.DisposeAsync();

        await using var lease = await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        lease.Lost.IsCancellationRequested.ShouldBeFalse();
        first.Lost.IsCancellationRequested.ShouldBeTrue("a released lease no longer leads");
    }

    [Fact]
    public async Task RevokeLeadership_CancelsLostBeforeTheNextCallerLeads()
    {
        using var elector = new InMemoryLeaderElector();
        var first = await elector.AcquireAsync(TestContext.Current.CancellationToken);
        var second = elector.AcquireAsync(TestContext.Current.CancellationToken);
        var lostWhenSecondLed = second.ContinueWith(
            _ => first.Lost.IsCancellationRequested,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        elector.RevokeLeadership();

        await using var lease = await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        (await lostWhenSecondLed).ShouldBeTrue("the old leader is told to stop before the new one starts");
        await first.DisposeAsync();
        elector.IsHeld.ShouldBeTrue("disposing a revoked lease must not release the new leader's lease");
    }

    [Fact]
    public async Task AcquireAsync_Cancelled_ThrowsAndDoesNotLead()
    {
        using var elector = new InMemoryLeaderElector();
        await using var first = await elector.AcquireAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();

        var waiting = elector.AcquireAsync(cancellation.Token);
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(waiting);
    }

    [Fact]
    public async Task AcquireAsync_ManyContenders_NeverTwoLeadersAtOnce()
    {
        using var elector = new InMemoryLeaderElector();
        var leaders = 0;
        var maxLeaders = 0;

        await Parallel.ForEachAsync(Enumerable.Range(0, 200), TestContext.Current.CancellationToken, async (_, cancellationToken) =>
        {
            await using var lease = await elector.AcquireAsync(cancellationToken);
            var now = Interlocked.Increment(ref leaders);
            InterlockedMax(ref maxLeaders, now);
            await Task.Yield();
            Interlocked.Decrement(ref leaders);
        });

        maxLeaders.ShouldBe(1);
        elector.IsHeld.ShouldBeFalse();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}
