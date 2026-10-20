using ElevatorSystem.Domain;
using Microsoft.Extensions.Logging;

namespace ElevatorSystem.Application.Occupancy;

/// <summary>
/// Source-generated log messages for occupancy counting (event IDs 1800-1819). They carry floors,
/// calls and counts only: passengers are anonymous and nothing identifies an individual (ADR-0024).
/// </summary>
internal static partial class OccupancyLog
{
    [LoggerMessage(1800, LogLevel.Warning, "{Requested} rider(s) were to get off at floor {Floor} but only {Riding} were riding; the count was clamped")]
    public static partial void RidersClamped(ILogger logger, Floor floor, int requested, int riding);

    [LoggerMessage(1801, LogLevel.Warning, "{Call} carried {Unseen} request(s) the tracker had not counted; they were counted as waiting")]
    public static partial void UncountedRequests(ILogger logger, ElevatorCall call, int unseen);

    [LoggerMessage(1802, LogLevel.Warning, "{Call} carried {Requests} request(s) but {Counted} were counted; the extra people keep waiting")]
    public static partial void MissingRequests(ILogger logger, ElevatorCall call, int requests, int counted);

    [LoggerMessage(1803, LogLevel.Warning, "Ignored {EventType} for floor {Floor}, which is outside the building")]
    public static partial void FloorOutsideBuilding(ILogger logger, string eventType, Floor floor);

    [LoggerMessage(1804, LogLevel.Information, "Out of service at floor {Floor}: {Riders} rider(s) got off there and {Waiting} waiting passenger(s) must call again")]
    public static partial void Evacuated(ILogger logger, Floor floor, int riders, int waiting);

    [LoggerMessage(1805, LogLevel.Error, "Occupancy observer failed; the counts keep being tracked")]
    public static partial void ObserverFailed(ILogger logger, Exception exception);

    [LoggerMessage(1806, LogLevel.Error, "Event sink {Sink} failed to accept {EventType}; the other sinks still receive it")]
    public static partial void SinkFailed(ILogger logger, Exception exception, string sink, string eventType);
}
