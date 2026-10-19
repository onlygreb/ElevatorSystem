using System.Diagnostics;

namespace ElevatorSystem.Application.InMemory.Messaging;

/// <summary>
/// Trace spans for the in-memory bus, on the same <see cref="ActivitySource"/> name as the RabbitMQ adapter,
/// so a trace continues from the publisher to the handler in one process just as it does across the broker.
/// </summary>
internal static class MessagingActivities
{
    public const string SourceName = "ElevatorSystem.Messaging";
    public const string CommandsDestination = "elevator.commands";
    public const string UpdatesDestination = "elevator.updates";

    private static readonly ActivitySource Source = new(SourceName);

    public static Activity? StartPublish(string destination, Guid messageId, string type) =>
        Start($"publish {destination}", ActivityKind.Producer, default, destination, messageId, type);

    public static Activity? StartProcess(string destination, Guid messageId, string type, ActivityContext parent) =>
        Start($"process {destination}", ActivityKind.Consumer, parent, destination, messageId, type);

    private static Activity? Start(string name, ActivityKind kind, ActivityContext parent, string destination, Guid messageId, string type)
    {
        var activity = Source.StartActivity(name, kind, parent);
        activity?.SetTag("messaging.system", "in-memory");
        activity?.SetTag("messaging.destination.name", destination);
        activity?.SetTag("messaging.message.id", messageId);
        activity?.SetTag("elevator.message.type", type);
        return activity;
    }
}
