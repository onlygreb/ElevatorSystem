namespace ElevatorSystem.Api;

/// <summary>
/// Source-generated log messages of the API host (event IDs 3000-3999). They never carry request
/// bodies, headers, cookies or client addresses (ADR-0009).
/// </summary>
internal static partial class ApiLog
{
    [LoggerMessage(3000, LogLevel.Warning,
        "Real-time buffer full: dropped {DroppedSinceLastReport} elevator event(s) ({TotalDropped} since start-up, capacity {Capacity}); clients resynchronise from the next status")]
    public static partial void RealTimeEventsDropped(ILogger logger, long droppedSinceLastReport, long totalDropped, int capacity);

    [LoggerMessage(3001, LogLevel.Warning, "Sending {Message} to real-time clients failed")]
    public static partial void BroadcastFailed(ILogger logger, Exception exception, string message);

    [LoggerMessage(3010, LogLevel.Information, "The staff database is ready")]
    public static partial void StaffDatabaseReady(ILogger logger);

    [LoggerMessage(3011, LogLevel.Warning, "The staff database is unavailable; retrying in {RetryInterval}")]
    public static partial void StaffDatabaseUnavailable(ILogger logger, Exception exception, TimeSpan retryInterval);

    [LoggerMessage(3012, LogLevel.Information, "Created a configured staff account with the {Role} role")]
    public static partial void StaffAccountCreated(ILogger logger, ElevatorSystem.Contracts.StaffRole role);

    [LoggerMessage(3013, LogLevel.Warning, "No Audit:ClientPseudonymKey is configured: client pseudonyms use a random key and link only until the next restart")]
    public static partial void EphemeralPseudonymKey(ILogger logger);

    [LoggerMessage(3014, LogLevel.Critical, "Preparing the staff database failed; staff sign-in stays unavailable until the API is restarted with a valid configuration")]
    public static partial void StaffPreparationFailed(ILogger logger, Exception exception);
}
