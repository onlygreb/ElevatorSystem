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

    public static async Task HoldsAsync(Func<bool> condition, string because, FakeTimeProvider? time = null, TimeSpan? step = null)
    {
        using var guard = new CancellationTokenSource(Guard);
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
}
