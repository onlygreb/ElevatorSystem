using System.Net;
using System.Net.Sockets;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace ElevatorSystem.Integration.Tests;

/// <summary>
/// One RabbitMQ and one PostgreSQL container for a test collection. Each test gets its own broker virtual host
/// and its own database, so no queue, message, row or advisory lock leaks from one test into the next. The
/// broker's host port is fixed when the container is created, so it stays the same if a test restarts it.
/// </summary>
public class SystemFixture : IAsyncLifetime
{
    /// <summary>The versions the AppHost runs, and the messaging and persistence tests use.</summary>
    public const string RabbitMqImage = "rabbitmq:4.1";

    public const string PostgresImage = "postgres:17.6-alpine";

    public SystemFixture()
    {
        BrokerPort = FreeTcpPort();
        Broker = new RabbitMqBuilder(RabbitMqImage).WithPortBinding(BrokerPort, RabbitMqBuilder.RabbitMqPort).Build();
        Database = new PostgreSqlBuilder(PostgresImage).Build();
    }

    public RabbitMqContainer Broker { get; }

    public int BrokerPort { get; }

    public PostgreSqlContainer Database { get; }

    public async ValueTask InitializeAsync() => await Task.WhenAll(Broker.StartAsync(), Database.StartAsync());

    public async ValueTask DisposeAsync()
    {
        await Broker.DisposeAsync();
        await Database.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>A fresh virtual host and database for one test.</summary>
    public async Task<TestEnvironment> CreateEnvironmentAsync()
    {
        var name = $"elevator_test_{Guid.NewGuid():N}";
        var brokerUri = new Uri(Broker.GetConnectionString());
        var user = Uri.UnescapeDataString(brokerUri.UserInfo.Split(':')[0]);
        await RabbitMqCtlAsync("add_vhost", name);
        await RabbitMqCtlAsync("set_permissions", "-p", name, user, ".*", ".*", ".*");

        await using (var connection = new NpgsqlConnection(Database.GetConnectionString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            // CREATE DATABASE takes no parameters; the name is generated above, never input.
            await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        return new TestEnvironment(
            new UriBuilder(brokerUri) { Host = "127.0.0.1", Port = BrokerPort, Path = "/" + name }.Uri,
            new NpgsqlConnectionStringBuilder(Database.GetConnectionString()) { Database = name }.ConnectionString);
    }

    private async Task RabbitMqCtlAsync(params string[] arguments)
    {
        var result = await Broker.ExecAsync(["rabbitmqctl", .. arguments], TestContext.Current.CancellationToken);
        result.ExitCode.ShouldBe(0, result.Stderr);
    }

    private static int FreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>Where one test's services find their broker virtual host and database.</summary>
public sealed record TestEnvironment(Uri BrokerUri, string DatabaseConnectionString);

/// <summary>The containers for most tests.</summary>
[CollectionDefinition(Name)]
public sealed class SystemGroup : ICollectionFixture<SystemFixture>
{
    public const string Name = "System";
}

/// <summary>Containers of their own for the test that stops the broker.</summary>
public sealed class RestartableSystemFixture : SystemFixture;

[CollectionDefinition(Name)]
public sealed class BrokerOutageGroup : ICollectionFixture<RestartableSystemFixture>
{
    public const string Name = "Broker outage";
}
