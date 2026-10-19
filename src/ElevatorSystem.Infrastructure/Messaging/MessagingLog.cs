using Microsoft.Extensions.Logging;

namespace ElevatorSystem.Infrastructure.Messaging;

/// <summary>
/// Source-generated log messages for the RabbitMQ adapter (event IDs 4000-4099). They name messages by ID and type
/// and the broker by an endpoint without credentials; message bodies are never logged (ADR-0009).
/// </summary>
internal static partial class MessagingLog
{
    [LoggerMessage(4000, LogLevel.Information, "Connected to the message broker at {Endpoint}; topology declared")]
    public static partial void Connected(ILogger logger, string endpoint);

    [LoggerMessage(4001, LogLevel.Warning, "Could not connect to the message broker at {Endpoint}")]
    public static partial void ConnectFailed(ILogger logger, Exception exception, string endpoint);

    [LoggerMessage(4002, LogLevel.Warning, "Lost the connection to the message broker ({Reason}); recovering automatically")]
    public static partial void ConnectionLost(ILogger logger, string reason);

    [LoggerMessage(4003, LogLevel.Information, "Connection to the message broker recovered")]
    public static partial void ConnectionRecovered(ILogger logger);

    [LoggerMessage(4004, LogLevel.Warning, "Connection recovery attempt failed; retrying")]
    public static partial void RecoveryFailed(ILogger logger, Exception exception);

    [LoggerMessage(4010, LogLevel.Debug, "Published {MessageType} {MessageId}; confirmed by the broker")]
    public static partial void Published(ILogger logger, Guid messageId, string messageType);

    [LoggerMessage(4011, LogLevel.Warning, "Publishing {MessageType} {MessageId} failed: {Reason}")]
    public static partial void PublishFailed(ILogger logger, Exception? exception, Guid messageId, string messageType, string reason);

    [LoggerMessage(4020, LogLevel.Information, "Consuming {Queue}")]
    public static partial void Consuming(ILogger logger, string queue);

    [LoggerMessage(4021, LogLevel.Warning, "Consumer channel on {Queue} closed; opening a new one")]
    public static partial void ConsumerChannelClosed(ILogger logger, string queue);

    [LoggerMessage(4022, LogLevel.Warning, "Could not start consuming {Queue}; retrying in {Delay}")]
    public static partial void ConsumerStartFailed(ILogger logger, Exception exception, string queue, TimeSpan delay);

    [LoggerMessage(4023, LogLevel.Error, "Rejected message {MessageId} of type {MessageType} from {Queue}: it is not a valid message; dead-lettered")]
    public static partial void Malformed(ILogger logger, string? messageId, string? messageType, string queue);

    [LoggerMessage(4024, LogLevel.Warning, "Handler failed for {MessageType} {MessageId} (delivery {DeliveryCount}); returned to the queue")]
    public static partial void CommandHandlerFailed(ILogger logger, Exception exception, Guid messageId, string messageType, long deliveryCount);

    [LoggerMessage(4025, LogLevel.Warning, "Handler failed for {MessageType} {MessageId} on attempt {Attempt}; retrying")]
    public static partial void UpdateHandlerFailed(ILogger logger, Exception exception, Guid messageId, string messageType, int attempt);

    [LoggerMessage(4026, LogLevel.Error, "Dead-lettered {MessageType} {MessageId} after {Attempts} failed attempts")]
    public static partial void UpdateDeadLettered(ILogger logger, Exception exception, Guid messageId, string messageType, int attempts);

    [LoggerMessage(4027, LogLevel.Warning, "Could not settle message {MessageId}; the broker will redeliver it")]
    public static partial void SettleFailed(ILogger logger, Exception exception, string? messageId);
}
