using System.Diagnostics;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Contracts;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Log = ElevatorSystem.Infrastructure.Messaging.MessagingLog;

namespace ElevatorSystem.Infrastructure.Messaging;

/// <summary>
/// Delivers commands from the engine's queue. The queue has a single active consumer, so a second engine instance
/// receives nothing until the first one's consumer goes away. A command is acknowledged only after the handler
/// completes; a handler failure returns it to the queue until the delivery limit dead-letters it; a message that is
/// not a valid command is dead-lettered at once. Delivery is at least once: handlers drop repeated command IDs.
/// </summary>
public sealed class RabbitMqElevatorCommandConsumer(RabbitMqConnection connection, ILogger<RabbitMqElevatorCommandConsumer> logger)
    : IElevatorCommandConsumer
{
    public Task ConsumeAsync(Func<ElevatorCommandV1, CancellationToken, Task> handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);

        return RabbitMqConsumerLoop.RunAsync(
            connection,
            logger,
            RabbitMqTopology.CommandsQueue,
            static (_, _) => Task.FromResult(RabbitMqTopology.CommandsQueue),
            (channel, delivery, token) => OnDeliveryAsync(channel, delivery, handle, token),
            cancellationToken);
    }

    private async Task OnDeliveryAsync(
        IChannel channel,
        BasicDeliverEventArgs delivery,
        Func<ElevatorCommandV1, CancellationToken, Task> handle,
        CancellationToken cancellationToken)
    {
        var properties = delivery.BasicProperties;
        if (!RabbitMqEnvelope.TryReadCommand(properties, delivery.Body, out var command))
        {
            // Retrying cannot fix a malformed message: straight to the dead-letter queue.
            Log.Malformed(logger, properties.MessageId, properties.Type, RabbitMqTopology.CommandsQueue);
            await RabbitMqConsumerLoop.SettleAsync(logger, delivery, () => channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, CancellationToken.None));
            return;
        }

        var type = RabbitMqEnvelope.TypeOf(command);
        using var activity = RabbitMqTelemetry.StartProcess(RabbitMqTopology.CommandsQueue, properties.MessageId, type, RabbitMqEnvelope.ExtractParent(properties));
        try
        {
            await handle(command, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping: leave it unacknowledged, so closing the channel returns it to the queue for the next consumer.
            return;
        }
        // Possibly transient: back to the queue; the broker dead-letters it once the delivery limit is reached.
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            Log.CommandHandlerFailed(logger, exception, command.CommandId, type, RabbitMqEnvelope.DeliveryCount(properties) + 1);
            await RabbitMqConsumerLoop.SettleAsync(logger, delivery, () => channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, CancellationToken.None));
            return;
        }

        await RabbitMqConsumerLoop.SettleAsync(logger, delivery, () => channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None));
    }
}
