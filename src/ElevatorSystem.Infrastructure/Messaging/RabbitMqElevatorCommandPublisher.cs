using System.Text.Json;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Contracts;

namespace ElevatorSystem.Infrastructure.Messaging;

/// <summary>
/// Publishes commands to the engine's queue. Completes only after the broker confirms the command is stored; the
/// publish is mandatory, so a command with no queue to go to is refused rather than silently dropped.
/// </summary>
public sealed class RabbitMqElevatorCommandPublisher(RabbitMqConnection connection) : IElevatorCommandPublisher
{
    public Task PublishAsync(ElevatorCommandV1 command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var body = JsonSerializer.SerializeToUtf8Bytes(command, ContractsJsonContext.Default.ElevatorCommandV1);
        return connection.PublishAsync(
            RabbitMqTopology.CommandsExchange,
            RabbitMqTopology.CommandRoutingKey,
            mandatory: true,
            command.CommandId,
            RabbitMqEnvelope.TypeOf(command),
            body,
            cancellationToken);
    }
}
