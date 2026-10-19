using System.Diagnostics;

namespace ElevatorSystem.Infrastructure.Messaging;

/// <summary>
/// Trace spans around publishing and handling messages. Hosts export them with
/// <c>AddSource(RabbitMqTelemetry.ActivitySourceName)</c>; the trace context crosses the broker in the
/// <c>traceparent</c> header, so a handler's span continues the publisher's trace.
/// </summary>
public static class RabbitMqTelemetry
{
    public const string ActivitySourceName = "ElevatorSystem.Messaging";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    internal static Activity? StartPublish(string exchange, Guid messageId, string type) =>
        Start($"publish {exchange}", ActivityKind.Producer, default, exchange, messageId.ToString(), type);

    internal static Activity? StartProcess(string queue, string? messageId, string? type, ActivityContext parent) =>
        Start($"process {queue}", ActivityKind.Consumer, parent, queue, messageId, type);

    private static Activity? Start(string name, ActivityKind kind, ActivityContext parent, string destination, string? messageId, string? type)
    {
        var activity = Source.StartActivity(name, kind, parent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", destination);
        activity?.SetTag("messaging.message.id", messageId);
        activity?.SetTag("elevator.message.type", type);
        return activity;
    }
}
