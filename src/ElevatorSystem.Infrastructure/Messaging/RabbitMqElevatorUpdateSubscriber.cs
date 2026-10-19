using System.Diagnostics;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Contracts;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Log = ElevatorSystem.Infrastructure.Messaging.MessagingLog;

namespace ElevatorSystem.Infrastructure.Messaging;

/// <summary>
/// Receives the engine's updates through a queue of its own for each subscription (see
/// <see cref="RabbitMqTopology.SubscriberQueue"/>), so every subscriber instance gets every update.
/// A handler that throws is retried a few times; then the update, like one that is not valid, is dead-lettered.
/// Updates published before the subscription's queue is bound, or while disconnected, are not received:
/// subscribers treat the next status as the truth.
/// </summary>
public sealed class RabbitMqElevatorUpdateSubscriber(RabbitMqConnection connection, ILogger<RabbitMqElevatorUpdateSubscriber> logger)
    : IElevatorUpdateSubscriber
{
    /// <summary>How many times an update is handed to a handler that throws before it is dead-lettered.</summary>
    public const int MaxHandlerAttempts = 3;

    public Task SubscribeAsync(
        Func<ElevatorUpdateV1, CancellationToken, Task> onElevatorUpdate,
        Func<OccupancyUpdateV1, CancellationToken, Task> onOccupancyUpdate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onElevatorUpdate);
        ArgumentNullException.ThrowIfNull(onOccupancyUpdate);

        var queue = RabbitMqTopology.SubscriberQueue(connection.Options.SubscriberName, Guid.CreateVersion7());
        return RabbitMqConsumerLoop.RunAsync(
            connection,
            logger,
            queue,
            async (channel, token) =>
            {
                await RabbitMqTopology.DeclareSubscriberQueueAsync(channel, queue, token);
                return queue;
            },
            (channel, delivery, token) => OnDeliveryAsync(channel, queue, delivery, onElevatorUpdate, onOccupancyUpdate, token),
            cancellationToken);
    }

    private async Task OnDeliveryAsync(
        IChannel channel,
        string queue,
        BasicDeliverEventArgs delivery,
        Func<ElevatorUpdateV1, CancellationToken, Task> onElevatorUpdate,
        Func<OccupancyUpdateV1, CancellationToken, Task> onOccupancyUpdate,
        CancellationToken cancellationToken)
    {
        var properties = delivery.BasicProperties;
        Func<CancellationToken, Task>? handle = null;
        var updateId = Guid.Empty;
        if (delivery.RoutingKey == RabbitMqTopology.ElevatorUpdateRoutingKey
            && RabbitMqEnvelope.TryRead(properties, delivery.Body, ContractsJsonContext.Default.ElevatorUpdateV1, out var elevatorUpdate))
        {
            (updateId, handle) = (elevatorUpdate.UpdateId, token => onElevatorUpdate(elevatorUpdate, token));
        }
        else if (delivery.RoutingKey == RabbitMqTopology.OccupancyUpdateRoutingKey
            && RabbitMqEnvelope.TryRead(properties, delivery.Body, ContractsJsonContext.Default.OccupancyUpdateV1, out var occupancyUpdate))
        {
            (updateId, handle) = (occupancyUpdate.UpdateId, token => onOccupancyUpdate(occupancyUpdate, token));
        }

        if (handle is null)
        {
            Log.Malformed(logger, properties.MessageId, properties.Type, queue);
            await DeadLetterAsync(channel, queue, delivery, "malformed");
            return;
        }

        var type = delivery.RoutingKey;
        for (var attempt = 1; ; attempt++)
        {
            using var activity = RabbitMqTelemetry.StartProcess(queue, properties.MessageId, type, RabbitMqEnvelope.ExtractParent(properties));
            try
            {
                await handle(cancellationToken);
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            // A failing handler must not stall the subscription: retried a few times, then set aside.
            catch (Exception exception)
            {
                activity?.SetStatus(ActivityStatusCode.Error);
                if (attempt >= MaxHandlerAttempts)
                {
                    Log.UpdateDeadLettered(logger, exception, updateId, type, attempt);
                    await DeadLetterAsync(channel, queue, delivery, "handler-failed");
                    return;
                }

                Log.UpdateHandlerFailed(logger, exception, updateId, type, attempt);
            }
        }

        await RabbitMqConsumerLoop.SettleAsync(logger, delivery, () => channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None));
    }

    /// <summary>
    /// Subscriber queues have no dead-letter exchange (their overflow would flood it), so a rejected update is
    /// copied to it here, with the reason and the queue it came from, and then acknowledged.
    /// </summary>
    private async Task DeadLetterAsync(IChannel channel, string queue, BasicDeliverEventArgs delivery, string reason)
    {
        var headers = new Dictionary<string, object?>(delivery.BasicProperties.Headers ?? new Dictionary<string, object?>())
        {
            [RabbitMqTopology.DeadLetterReasonHeader] = reason,
            [RabbitMqTopology.DeadLetterSourceQueueHeader] = queue,
        };
        var properties = new BasicProperties(delivery.BasicProperties) { Headers = headers };

        await RabbitMqConsumerLoop.SettleAsync(logger, delivery, async () =>
        {
            await channel.BasicPublishAsync(RabbitMqTopology.DeadLetterExchange, delivery.RoutingKey, mandatory: false, properties, delivery.Body, CancellationToken.None);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None);
        });
    }
}
