using System.Net;
using System.Net.Sockets;
using System.Text;
using ElevatorSystem.Infrastructure.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace ElevatorSystem.Messaging.Tests;

/// <summary>
/// One RabbitMQ container shared by a test collection (tests in a collection run one at a time). The broker's host
/// port is fixed when the container is created, so it stays the same if a test restarts the container.
/// </summary>
public class RabbitMqFixture : IAsyncLifetime
{
    /// <summary>The broker version the services run against; Aspire's RabbitMQ resource uses the same major version.</summary>
    public const string Image = "rabbitmq:4.1";

    private readonly List<IAsyncDisposable> _owned = [];

    public RabbitMqFixture()
    {
        HostPort = FreeTcpPort();
        Container = new RabbitMqBuilder(Image)
            .WithPortBinding(HostPort, RabbitMqBuilder.RabbitMqPort)
            .Build();
    }

    public RabbitMqContainer Container { get; }

    public int HostPort { get; }

    /// <summary>Every log record the adapter wrote during the collection.</summary>
    public FakeLogCollector Logs { get; } = new();

    public Uri BrokerUri => new(Container.GetConnectionString());

    public async ValueTask InitializeAsync() => await Container.StartAsync();

    public async ValueTask DisposeAsync()
    {
        foreach (var owned in _owned)
        {
            await owned.DisposeAsync();
        }

        await Container.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>A connection as a service would hold one, with timeouts short enough for tests.</summary>
    public RabbitMqConnection CreateConnection(Action<MessagingOptions>? configure = null)
    {
        var options = new MessagingOptions
        {
            Uri = BrokerUri,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PublishTimeout = TimeSpan.FromSeconds(10),
            RecoveryInterval = TimeSpan.FromSeconds(1),
            PrefetchCount = 8,
        };
        configure?.Invoke(options);
        var connection = new RabbitMqConnection(Options.Create(options), Logger<RabbitMqConnection>(), TimeProvider.System);
        _owned.Add(connection);
        return connection;
    }

    public ILogger<T> Logger<T>() => new FakeLogger<T>(Logs);

    /// <summary>Writes what the adapter logged during the test to its output (shown when it fails), then starts afresh.</summary>
    public void ReportLogs()
    {
        foreach (var record in Logs.GetSnapshot(clear: true))
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"{record.Timestamp:HH:mm:ss.fff} {record.Level} {record.Category}: {record.Message} {record.Exception?.GetType().Name} {record.Exception?.Message}");
        }
    }

    /// <summary>Empties the shared queues so one test's leftovers never reach the next.</summary>
    public async Task ResetAsync()
    {
        // Connecting declares the topology, in case no test has yet.
        await CreateConnection().GetConnectionAsync(TestContext.Current.CancellationToken);
        var (_, channel) = await OpenRawChannelAsync();
        // A consumer left behind would hold the single-active-consumer slot and starve the next test.
        await Eventually.IsTrueAsync(
            async () => (await channel.QueueDeclarePassiveAsync(RabbitMqTopology.CommandsQueue, TestContext.Current.CancellationToken)).ConsumerCount == 0,
            "the previous test's consumers to stop",
            TimeSpan.FromSeconds(10));
        await channel.QueuePurgeAsync(RabbitMqTopology.CommandsQueue, TestContext.Current.CancellationToken);
        await channel.QueuePurgeAsync(RabbitMqTopology.DeadLetterQueue, TestContext.Current.CancellationToken);
    }

    /// <summary>A plain client connection, to publish raw bytes or inspect queues as an operator would.</summary>
    public async Task<(IConnection Connection, IChannel Channel)> OpenRawChannelAsync()
    {
        var connection = await new ConnectionFactory { Uri = BrokerUri }.CreateConnectionAsync(TestContext.Current.CancellationToken);
        var channel = await connection.CreateChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
        _owned.Add(connection);
        return (connection, channel);
    }

    /// <summary>Waits until a message with <paramref name="messageId"/> reaches the dead-letter queue, and returns it.</summary>
    public async Task<BasicGetResult> WaitForDeadLetterAsync(string messageId)
    {
        var (_, channel) = await OpenRawChannelAsync();
        return await Eventually.ReturnsAsync(
            async () =>
            {
                while (await channel.BasicGetAsync(RabbitMqTopology.DeadLetterQueue, autoAck: true, TestContext.Current.CancellationToken) is { } message)
                {
                    if (message.BasicProperties.MessageId == messageId)
                    {
                        return message;
                    }
                }

                return null;
            },
            $"message {messageId} in {RabbitMqTopology.DeadLetterQueue}");
    }

    /// <summary>
    /// Drops a client's connection from the broker side, as a crashed process or a broken network would: the client
    /// gets no chance to finish or acknowledge anything.
    /// </summary>
    public async Task KillConnectionAsync(string clientName)
    {
        var listed = await Container.ExecAsync(["rabbitmqctl", "list_connections", "--silent", "--no-table-headers", "pid", "client_properties"], TestContext.Current.CancellationToken);
        var line = listed.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .SingleOrDefault(candidate => candidate.Contains(clientName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No connection named {clientName} in: {listed.Stdout} {listed.Stderr}");
        var pid = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries)[0];

        var closed = await Container.ExecAsync(["rabbitmqctl", "close_connection", pid, "killed by test"], TestContext.Current.CancellationToken);
        closed.ExitCode.ShouldBe(0, closed.Stderr);
    }

    /// <summary>All consumers registered on a queue, active or waiting.</summary>
    public async Task<int> CountConsumersAsync(string queue)
    {
        var listed = await Container.ExecAsync(["rabbitmqctl", "list_consumers", "--silent", "queue_name"], TestContext.Current.CancellationToken);
        return listed.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(line => line.Trim() == queue);
    }

    public static string Header(IReadOnlyBasicProperties properties, string name) =>
        properties.Headers?.TryGetValue(name, out var value) == true && value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : string.Empty;

    private static int FreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>The shared broker for most tests.</summary>
[CollectionDefinition(Name)]
public sealed class SharedBrokerTests : ICollectionFixture<RabbitMqFixture>
{
    public const string Name = "RabbitMQ";
}

/// <summary>A broker of its own for the tests that stop and restart it.</summary>
public sealed class RestartableRabbitMqFixture : RabbitMqFixture;

[CollectionDefinition(Name)]
public sealed class RestartableBrokerTests : ICollectionFixture<RestartableRabbitMqFixture>
{
    public const string Name = "RabbitMQ restart";
}
