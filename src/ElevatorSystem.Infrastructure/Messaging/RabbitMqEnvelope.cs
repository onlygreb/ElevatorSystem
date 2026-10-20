using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ElevatorSystem.Contracts;
using RabbitMQ.Client;

namespace ElevatorSystem.Infrastructure.Messaging;

/// <summary>
/// The message envelope (messaging skill): persistent JSON bodies from <see cref="ContractsJsonContext"/>, with
/// <c>message-id</c> (the command or update ID, so consumers can drop redeliveries), <c>correlation-id</c> (the trace
/// ID when there is one), <c>content-type</c>, the message type, and the W3C <c>traceparent</c> header.
/// </summary>
internal static class RabbitMqEnvelope
{
    public const string ContentType = "application/json";

    public const string ElevatorUpdateType = RabbitMqTopology.ElevatorUpdateRoutingKey;

    public const string OccupancyUpdateType = RabbitMqTopology.OccupancyUpdateRoutingKey;

    /// <summary>The broker's count of earlier failed deliveries of a quorum-queue message.</summary>
    public const string DeliveryCountHeader = "x-delivery-count";

    /// <summary>Contract messages are a few hundred bytes; anything far larger is refused unread.</summary>
    public const int MaxBodyBytes = 64 * 1024;

    public static string TypeOf(ElevatorCommandV1 command) => command switch
    {
        HallCallCommandV1 => "hall-call.v1",
        CarCallCommandV1 => "car-call.v1",
        ServiceCommandV1 => "service.v1",
        PassengerCommandV1 => "passenger.v1",
        ScenarioCommandV1 => "scenario.v1",
        _ => throw new ArgumentException($"Unknown command type {command.GetType().Name}.", nameof(command)),
    };

    public static BasicProperties CreateProperties(Guid messageId, string type, DateTimeOffset now)
    {
        var properties = new BasicProperties
        {
            Persistent = true,
            MessageId = messageId.ToString(),
            CorrelationId = Activity.Current?.TraceId.ToHexString() ?? messageId.ToString(),
            ContentType = ContentType,
            Type = type,
            Timestamp = new AmqpTimestamp(now.ToUnixTimeSeconds()),
            Headers = new Dictionary<string, object?>(),
        };

        if (Activity.Current is { } activity)
        {
            DistributedContextPropagator.Current.Inject(activity, properties.Headers, static (carrier, name, value) =>
            {
                if (carrier is IDictionary<string, object?> headers)
                {
                    headers[name] = value;
                }
            });
        }

        return properties;
    }

    /// <summary>The trace context the publisher sent, or <c>default</c> if there is none or it is malformed.</summary>
    public static ActivityContext ExtractParent(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers is null)
        {
            return default;
        }

        DistributedContextPropagator.Current.ExtractTraceIdAndState(
            properties.Headers,
            static (object? carrier, string name, out string? value, out IEnumerable<string>? values) =>
            {
                values = null;
                value = carrier is IDictionary<string, object?> headers ? ReadHeader(headers, name) : null;
            },
            out var traceParent,
            out var traceState);

        return ActivityContext.TryParse(traceParent, traceState, isRemote: true, out var context) ? context : default;
    }

    public static long DeliveryCount(IReadOnlyBasicProperties properties) =>
        properties.Headers?.TryGetValue(DeliveryCountHeader, out var value) == true
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
            : 0;

    public static bool TryReadCommand(IReadOnlyBasicProperties properties, ReadOnlyMemory<byte> body, out ElevatorCommandV1 command)
    {
        command = null!;
        if (!TryRead(properties, body, ContractsJsonContext.Default.ElevatorCommandV1, out var read))
        {
            return false;
        }

        // Consumers treat every message as untrusted input: enums must name a defined value.
        var wellFormed = read.CommandId != Guid.Empty && read switch
        {
            HallCallCommandV1 hall => Enum.IsDefined(hall.Direction),
            CarCallCommandV1 => true,
            ServiceCommandV1 service => Enum.IsDefined(service.Action),
            PassengerCommandV1 passenger => passenger.PassengerId != Guid.Empty && Enum.IsDefined(passenger.Action)
                && (passenger.Direction is null || Enum.IsDefined(passenger.Direction.Value)),
            ScenarioCommandV1 scenario => scenario.Scenario is null || Enum.IsDefined(scenario.Scenario.Value),
            _ => false,
        };

        command = read;
        return wellFormed;
    }

    public static bool TryRead<T>(IReadOnlyBasicProperties properties, ReadOnlyMemory<byte> body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, out T message)
        where T : class
    {
        message = null!;
        if (body.Length > MaxBodyBytes || properties.ContentType is not ContentType)
        {
            return false;
        }

        try
        {
            message = JsonSerializer.Deserialize(body.Span, typeInfo)!;
            return message is not null;
        }
        // NotSupportedException: no "type" discriminator for the abstract command type.
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private static string? ReadHeader(IDictionary<string, object?> headers, string name) =>
        headers.TryGetValue(name, out var value)
            ? value switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string text => text,
                _ => null,
            }
            : null;
}
