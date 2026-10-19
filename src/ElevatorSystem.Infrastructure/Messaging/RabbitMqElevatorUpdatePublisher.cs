using System.Text.Json;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Contracts;

namespace ElevatorSystem.Infrastructure.Messaging;

/// <summary>
/// Publishes the engine's updates to the topic exchange, routed by update type to every subscriber queue. Completes
/// after the broker's confirm. Not mandatory: an update with no subscriber is simply not needed.
/// </summary>
public sealed class RabbitMqElevatorUpdatePublisher(RabbitMqConnection connection) : IElevatorUpdatePublisher
{
    public Task PublishAsync(ElevatorUpdateV1 update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        var body = JsonSerializer.SerializeToUtf8Bytes(update, ContractsJsonContext.Default.ElevatorUpdateV1);
        return connection.PublishAsync(
            RabbitMqTopology.UpdatesExchange,
            RabbitMqTopology.ElevatorUpdateRoutingKey,
            mandatory: false,
            update.UpdateId,
            RabbitMqEnvelope.ElevatorUpdateType,
            body,
            cancellationToken);
    }

    public Task PublishAsync(OccupancyUpdateV1 update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        var body = JsonSerializer.SerializeToUtf8Bytes(update, ContractsJsonContext.Default.OccupancyUpdateV1);
        return connection.PublishAsync(
            RabbitMqTopology.UpdatesExchange,
            RabbitMqTopology.OccupancyUpdateRoutingKey,
            mandatory: false,
            update.UpdateId,
            RabbitMqEnvelope.OccupancyUpdateType,
            body,
            cancellationToken);
    }
}
