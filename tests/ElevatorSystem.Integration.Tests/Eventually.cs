namespace ElevatorSystem.Integration.Tests;

/// <summary>
/// Polls a condition another process makes true, with a real-time guard so a broken system fails with a
/// clear message instead of hanging. Never a fixed sleep: it returns as soon as the condition holds.
/// </summary>
internal static class Eventually
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    public static Task HoldsAsync(Func<bool> condition, string because, TimeSpan? timeout = null) =>
        HoldsAsync(() => Task.FromResult(condition()), because, timeout);

    public static async Task HoldsAsync(Func<Task<bool>> condition, string because, TimeSpan? timeout = null)
    {
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        guard.CancelAfter(timeout ?? DefaultTimeout);
        while (!await condition())
        {
            if (guard.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for {because}.");
            }

            await Task.Delay(PollInterval, CancellationToken.None);
        }
    }

    public static async Task<T> ReturnsAsync<T>(Func<Task<T?>> attempt, string because, TimeSpan? timeout = null)
        where T : class
    {
        T? result = null;
        await HoldsAsync(async () => (result = await attempt()) is not null, because, timeout);
        return result!;
    }
}
