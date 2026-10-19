using Microsoft.AspNetCore.SignalR.Client;

namespace ElevatorSystem.Web.Services;

/// <summary>Retry schedule for the hub: quick at first, then every 30 seconds, forever.</summary>
internal sealed class ReconnectBackoff : IRetryPolicy
{
    private static readonly TimeSpan[] Schedule =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30),
    ];

    public static TimeSpan Delay(long previousAttempts) =>
        Schedule[(int)Math.Clamp(previousAttempts, 0, Schedule.Length - 1)];

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        ArgumentNullException.ThrowIfNull(retryContext);
        return Delay(retryContext.PreviousRetryCount);
    }
}
