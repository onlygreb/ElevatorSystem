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
}
