using ElevatorSystem.Domain;
using Microsoft.Extensions.Logging;

namespace ElevatorSystem.Application;

/// <summary>
/// Source-generated log messages for the controller (event IDs 1500-1999). They describe floors,
/// states, calls and request IDs only: the elevator core handles no personal data (ADR-0024).
/// </summary>
internal static partial class ElevatorControllerLog
{
    [LoggerMessage(1500, LogLevel.Information, "Control loop started at floor {Floor} using {Strategy} scheduling")]
    public static partial void ControlLoopStarted(ILogger logger, Floor floor, string strategy);

    [LoggerMessage(1501, LogLevel.Information, "Control loop stopped")]
    public static partial void ControlLoopStopped(ILogger logger);

    [LoggerMessage(1502, LogLevel.Information, "Request refused: {Call} ({Reason})")]
    public static partial void RequestRejected(ILogger logger, ElevatorCall call, RequestRejectionReason reason);

    [LoggerMessage(1503, LogLevel.Information, "Queued {Call} as request {RequestId} at position {Position}")]
    public static partial void CallQueued(ILogger logger, ElevatorCall call, RequestId requestId, int position);

    [LoggerMessage(1504, LogLevel.Debug, "Merged request {RequestId} into pending {Call} at position {Position}")]
    public static partial void CallMerged(ILogger logger, RequestId requestId, ElevatorCall call, int position);

    [LoggerMessage(1505, LogLevel.Information, "Moving {Direction} from floor {From} to floor {To}")]
    public static partial void ElevatorMoved(ILogger logger, Direction direction, Floor from, Floor to);

    [LoggerMessage(1506, LogLevel.Information, "Doors open at floor {Floor}")]
    public static partial void DoorsOpened(ILogger logger, Floor floor);

    [LoggerMessage(1507, LogLevel.Information, "Served {Call} ({RequestCount} request(s)) after {WaitTime}")]
    public static partial void CallServed(ILogger logger, ElevatorCall call, int requestCount, TimeSpan waitTime);

    [LoggerMessage(1508, LogLevel.Information, "Doors closed at floor {Floor}")]
    public static partial void DoorsClosed(ILogger logger, Floor floor);

    [LoggerMessage(1509, LogLevel.Warning, "Elevator out of service at floor {Floor}: {Reason}; {CancelledCalls} pending call(s) cancelled")]
    public static partial void TakenOutOfService(ILogger logger, Floor floor, OutOfServiceReason reason, int cancelledCalls);

    [LoggerMessage(1510, LogLevel.Information, "Elevator returned to service at floor {Floor}")]
    public static partial void ReturnedToService(ILogger logger, Floor floor);

    [LoggerMessage(1511, LogLevel.Error, "No arrival at floor {Floor} within {Timeout}: the car is considered stuck")]
    public static partial void ElevatorStuck(ILogger logger, Floor floor, TimeSpan timeout);

    [LoggerMessage(1512, LogLevel.Error, "The drive failed while travelling to floor {Floor}")]
    public static partial void DriveFailed(ILogger logger, Exception exception, Floor floor);

    [LoggerMessage(1513, LogLevel.Critical, "Unexpected failure in the control loop; taking the elevator out of service")]
    public static partial void ControlLoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(1514, LogLevel.Error, "Event sink failed to accept {EventType}; the elevator keeps operating")]
    public static partial void EventSinkFailed(ILogger logger, Exception exception, string eventType);

    [LoggerMessage(1515, LogLevel.Error, "Failed to apply request {RequestId}; it was dropped")]
    public static partial void RequestFailed(ILogger logger, Exception exception, RequestId requestId);

    [LoggerMessage(1516, LogLevel.Warning, "{Transition} refused at floor {Floor}: {Error}")]
    public static partial void TransitionRefused(ILogger logger, string transition, Floor floor, TransitionError error);

    [LoggerMessage(1517, LogLevel.Error, "Event sink {Sink} failed to accept {EventType}; the other sinks still receive it")]
    public static partial void SinkFailed(ILogger logger, Exception exception, string sink, string eventType);

    [LoggerMessage(1518, LogLevel.Information, "The simulated car now runs at {Speed}x normal speed")]
    public static partial void SpeedChanged(ILogger logger, double speed);

    [LoggerMessage(1519, LogLevel.Information, "Queue cleared: {Cancelled} pending call(s) cancelled")]
    public static partial void RequestsCleared(ILogger logger, int cancelled);

    [LoggerMessage(1520, LogLevel.Information, "Scheduling switched to {Policy}")]
    public static partial void SchedulingChanged(ILogger logger, SchedulingPolicy policy);
}
