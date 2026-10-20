using Microsoft.Extensions.Time.Testing;

namespace ElevatorSystem.Api.Tests;

/// <summary>
/// Polls a condition that another thread makes true, with a real-time guard so a broken host fails
/// fast instead of hanging. Never a fixed sleep: it returns as soon as the condition holds.
/// </summary>
internal static class Eventually
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(5);

    /// <param name="condition">What must become true.</param>
    /// <param name="because">What is awaited, for the failure message.</param>
    /// <param name="time">A fake clock to advance by <paramref name="step"/> between polls, if the condition needs time to pass.</param>
    /// <param name="step">How far to advance <paramref name="time"/> per poll (default 100 ms).</param>
    /// <param name="cancellationToken">Ends the wait early, for example when the test times out.</param>
    public static async Task HoldsAsync(
        Func<bool> condition, string because, FakeTimeProvider? time = null, TimeSpan? step = null, CancellationToken cancellationToken = default)
    {
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        guard.CancelAfter(Guard);
        while (!condition())
        {
            if (guard.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for {because}.");
            }

            time?.Advance(step ?? TimeSpan.FromMilliseconds(100));
            await Task.Delay(PollInterval, CancellationToken.None);
        }
    }

    /// <summary>Polls <paramref name="attempt"/> until it returns a value, and returns it.</summary>
    public static async Task<T> ReturnsAsync<T>(Func<Task<T?>> attempt, string because)
        where T : class
    {
        using var guard = new CancellationTokenSource(Guard);
        while (true)
        {
            if (await attempt() is { } result)
            {
                return result;
            }

            if (guard.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for {because}.");
            }

            await Task.Delay(PollInterval, CancellationToken.None);
        }
    }
}
