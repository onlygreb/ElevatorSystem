using Microsoft.Extensions.Logging;

namespace ElevatorSystem.Application.InMemory.Messaging;

/// <summary>
/// Source-generated log messages for the in-memory bus (event IDs 4100-4199). Message IDs and types only:
/// message bodies are never logged (ADR-0009).
/// </summary>
internal static partial class InMemoryMessagingLog
{
    [LoggerMessage(4100, LogLevel.Debug, "Published {MessageType} {MessageId}")]
    public static partial void Published(ILogger logger, Guid messageId, string messageType);

    [LoggerMessage(4101, LogLevel.Warning, "Refused {MessageType} {MessageId}: the command queue is full")]
    public static partial void CommandRefused(ILogger logger, Guid messageId, string messageType);

    [LoggerMessage(4102, LogLevel.Warning, "Handler failed for {MessageType} {MessageId} on attempt {Attempt}; retrying")]
    public static partial void HandlerFailed(ILogger logger, Exception exception, Guid messageId, string messageType, int attempt);

    [LoggerMessage(4103, LogLevel.Error, "Dropped {MessageType} {MessageId} after {Attempts} failed attempts")]
    public static partial void Dropped(ILogger logger, Exception exception, Guid messageId, string messageType, int attempts);
}
