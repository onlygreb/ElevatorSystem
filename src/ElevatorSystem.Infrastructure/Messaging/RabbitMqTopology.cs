using RabbitMQ.Client;

namespace ElevatorSystem.Infrastructure.Messaging;

/// <summary>
/// Exchanges and queues (messaging skill, ADR-0018). Declarations are idempotent: every connection declares them
/// again with the same arguments, and a mismatch with an existing queue fails loudly instead of being ignored.
/// </summary>
public static class RabbitMqTopology
{
    /// <summary>Direct exchange for commands to the engine.</summary>
    public const string CommandsExchange = "elevator.commands";

    /// <summary>The engine's command queue: quorum, single active consumer, delivery limit, dead-lettered.</summary>
    public const string CommandsQueue = "elevator.commands.engine";

    public const string CommandRoutingKey = "command.v1";

    /// <summary>Topic exchange for the engine's updates; the routing key is the update type.</summary>
    public const string UpdatesExchange = "elevator.updates";

    public const string ElevatorUpdateRoutingKey = "elevator-update.v1";

    public const string OccupancyUpdateRoutingKey = "occupancy-update.v1";

    /// <summary>Fanout exchange receiving every dead-lettered message.</summary>
    public const string DeadLetterExchange = "elevator.dlx";

    /// <summary>Any message here is a bug or a poison message; operators alert on it.</summary>
    public const string DeadLetterQueue = "elevator.deadletter";

    /// <summary>Header on an update the subscriber dead-lettered itself: <c>malformed</c> or <c>handler-failed</c>.</summary>
    public const string DeadLetterReasonHeader = "x-elevator-dead-letter-reason";

    /// <summary>Header naming the subscriber queue such an update came from.</summary>
    public const string DeadLetterSourceQueueHeader = "x-elevator-source-queue";

    /// <summary>A command whose handler fails this many times is dead-lettered.</summary>
    public const int DeliveryLimit = 5;

    /// <summary>Updates a subscriber queue holds before it drops the oldest; the next status supersedes them.</summary>
    public const int SubscriberQueueLength = 1000;

    /// <summary>Queue name for one subscriber instance.</summary>
    public static string SubscriberQueue(string subscriberName, Guid instanceId) =>
        $"{UpdatesExchange}.{subscriberName}.{instanceId:N}";

    internal static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(
            DeadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" },
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(DeadLetterQueue, DeadLetterExchange, routingKey: string.Empty, cancellationToken: cancellationToken);

        await channel.ExchangeDeclareAsync(CommandsExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(
            CommandsQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                // Only one engine instance ever receives commands; a standby takes over when it goes away.
                ["x-single-active-consumer"] = true,
                ["x-delivery-limit"] = DeliveryLimit,
                ["x-dead-letter-exchange"] = DeadLetterExchange,
                // The broker keeps a dead-lettered command until the dead-letter queue has confirmed it.
                ["x-dead-letter-strategy"] = "at-least-once",
                ["x-overflow"] = "reject-publish",
            },
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(CommandsQueue, CommandsExchange, CommandRoutingKey, cancellationToken: cancellationToken);

        await channel.ExchangeDeclareAsync(UpdatesExchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// A queue for one subscriber instance: exclusive to its connection and removed with it (declared again on
    /// recovery), and bounded, dropping the oldest update when full. It has no dead-letter exchange, so dropped
    /// updates do not flood the dead-letter queue; the subscriber dead-letters the updates it rejects itself.
    /// </summary>
    internal static async Task DeclareSubscriberQueueAsync(IChannel channel, string queue, CancellationToken cancellationToken)
    {
        await channel.QueueDeclareAsync(
            queue,
            durable: false,
            exclusive: true,
            autoDelete: true,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "classic",
                ["x-max-length"] = SubscriberQueueLength,
                ["x-overflow"] = "drop-head",
            },
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(queue, UpdatesExchange, ElevatorUpdateRoutingKey, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(queue, UpdatesExchange, OccupancyUpdateRoutingKey, cancellationToken: cancellationToken);
    }
}
