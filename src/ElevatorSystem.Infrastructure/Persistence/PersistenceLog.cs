using Microsoft.Extensions.Logging;

namespace ElevatorSystem.Infrastructure.Persistence;

/// <summary>
/// Source-generated log messages for persistence and leader election (event IDs 4500-4599). They
/// carry versions, counts, lock keys and intervals only: no connection strings and no personal data (ADR-0009).
/// </summary>
internal static partial class PersistenceLog
{
    [LoggerMessage(4500, LogLevel.Debug, "Ignored snapshot version {Version}: a newer snapshot is already saved")]
    public static partial void StaleSnapshotIgnored(ILogger logger, long version);

    [LoggerMessage(4510, LogLevel.Information, "Standing by: another instance holds leader lock {LockId}")]
    public static partial void StandingBy(ILogger logger, long lockId);

    [LoggerMessage(4511, LogLevel.Warning, "Could not compete for leader lock {LockId}; retrying in {RetryInterval}")]
    public static partial void LeadershipAttemptFailed(ILogger logger, Exception exception, long lockId, TimeSpan retryInterval);

    [LoggerMessage(4512, LogLevel.Information, "Took leader lock {LockId}; waiting {TakeoverDelay} for any previous leader to stop")]
    public static partial void LeaderLockTaken(ILogger logger, long lockId, TimeSpan takeoverDelay);

    [LoggerMessage(4513, LogLevel.Information, "Leadership acquired (lock {LockId})")]
    public static partial void LeadershipAcquired(ILogger logger, long lockId);

    [LoggerMessage(4514, LogLevel.Warning, "Leadership lost (lock {LockId}): the lock connection failed its check")]
    public static partial void LeadershipLost(ILogger logger, Exception exception, long lockId);

    [LoggerMessage(4515, LogLevel.Information, "Leadership released (lock {LockId})")]
    public static partial void LeadershipReleased(ILogger logger, long lockId);

    [LoggerMessage(4516, LogLevel.Warning, "Could not unlock leader lock {LockId}; closing the connection releases it")]
    public static partial void LeadershipReleaseFailed(ILogger logger, Exception exception, long lockId);

    [LoggerMessage(4520, LogLevel.Information, "Purged {Count} processed command ID(s) older than {Retention}")]
    public static partial void ProcessedCommandsPurged(ILogger logger, int count, TimeSpan retention);

    [LoggerMessage(4521, LogLevel.Warning, "Purging processed command IDs failed; retrying in {PurgeInterval}")]
    public static partial void ProcessedCommandPurgeFailed(ILogger logger, Exception exception, TimeSpan purgeInterval);
}
