using ElevatorSystem.Application.Ports;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using Log = ElevatorSystem.Infrastructure.Messaging.MessagingLog;

namespace ElevatorSystem.Infrastructure.Messaging;

/// <summary>
/// Keeps one consumer running on a queue until cancelled: manual acknowledgement, bounded prefetch, one message at a
/// time. A lost connection is left to automatic recovery, which restores the channel and the consumer; a channel the
/// broker closed while the connection stayed up, or a failure to start, is replaced after the recovery interval.
/// </summary>
internal static class RabbitMqConsumerLoop
{
    public static async Task RunAsync(
        RabbitMqConnection connection,
        ILogger logger,
        string queueDescription,
        Func<IChannel, CancellationToken, Task<string>> declareQueue,
        Func<IChannel, BasicDeliverEventArgs, CancellationToken, Task> onDelivery,
        CancellationToken cancellationToken)
    {
        var delay = connection.Options.RecoveryInterval;
        while (!cancellationToken.IsCancellationRequested)
        {
            IChannel? channel = null;
            try
            {
                var current = await connection.GetConnectionAsync(cancellationToken);
                channel = await current.CreateChannelAsync(cancellationToken: cancellationToken);
                await channel.BasicQosAsync(prefetchSize: 0, connection.Options.PrefetchCount, global: false, cancellationToken);
                var queue = await declareQueue(channel, cancellationToken);

                var closedForGood = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                channel.ChannelShutdownAsync += (_, _) =>
                {
                    // With the connection still up, the broker closed this channel itself and recovery won't restore it.
                    if (current.IsOpen)
                    {
                        closedForGood.TrySetResult();
                    }

                    return Task.CompletedTask;
                };

                var consumer = new AsyncEventingBasicConsumer(channel);
                var consumerChannel = channel;
                consumer.ReceivedAsync += (_, delivery) => onDelivery(consumerChannel, delivery, cancellationToken);
                await channel.BasicConsumeAsync(queue, autoAck: false, consumer, cancellationToken);
                Log.Consuming(logger, queue);

                await closedForGood.Task.WaitAsync(cancellationToken);
                Log.ConsumerChannelClosed(logger, queue);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            // Broker down, recovering or refusing the declaration: try again later rather than stop consuming.
            catch (Exception exception) when (exception is MessagingUnavailableException or RabbitMQClientException or IOException or TimeoutException)
            {
                Log.ConsumerStartFailed(logger, exception, queueDescription, delay);
                try
                {
                    await Task.Delay(delay, connection.TimeProvider, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                // Closing the channel returns any unacknowledged message to the queue.
                if (channel is not null)
                {
                    await RabbitMqConnection.CloseQuietlyAsync(channel);
                }
            }
        }
    }

    /// <summary>Acknowledges or rejects a delivery; if the channel is gone, the broker redelivers the message anyway.</summary>
    public static async Task SettleAsync(ILogger logger, BasicDeliverEventArgs delivery, Func<ValueTask> settle)
    {
        try
        {
            await settle();
        }
        catch (Exception exception) when (exception is RabbitMQClientException or IOException or ObjectDisposedException)
        {
            Log.SettleFailed(logger, exception, delivery.BasicProperties.MessageId);
        }
    }
}
