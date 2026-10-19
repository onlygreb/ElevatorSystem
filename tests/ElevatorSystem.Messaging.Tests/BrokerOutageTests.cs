using System.Net;
using System.Net.Sockets;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Contracts;
using ElevatorSystem.Infrastructure.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ElevatorSystem.Messaging.Tests;

/// <summary>Without a broker nothing is reported as accepted, and health says so.</summary>
public sealed class UnreachableBrokerTests
{
    [Fact]
    public async Task PublishAsync_BrokerUnreachable_ThrowsMessagingUnavailable()
    {
        await using var connection = UnreachableConnection();
        var publisher = new RabbitMqElevatorCommandPublisher(connection);

        var exception = await Should.ThrowAsync<MessagingUnavailableException>(
            () => publisher.PublishAsync(TestMessages.Car(3), TestContext.Current.CancellationToken));

        exception.Message.ShouldNotContain("secret-password");
    }

    [Fact]
    public async Task UpdatePublishAsync_BrokerUnreachable_ThrowsMessagingUnavailable()
    {
        await using var connection = UnreachableConnection();
        var publisher = new RabbitMqElevatorUpdatePublisher(connection);

        await Should.ThrowAsync<MessagingUnavailableException>(
            () => publisher.PublishAsync(TestMessages.ElevatorUpdate(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HealthCheck_BrokerUnreachable_IsDegraded()
    {
        await using var connection = UnreachableConnection();

        var result = await new RabbitMqHealthCheck(connection).CheckHealthAsync(HealthContext(), TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealthStatus.Degraded);
    }

    [Fact]
    public async Task ConsumeAsync_BrokerUnreachable_KeepsRetryingUntilCancelled()
    {
        await using var connection = UnreachableConnection();
        var consumer = new RabbitMqElevatorCommandConsumer(connection, NullLogger<RabbitMqElevatorCommandConsumer>.Instance);
        using var stop = new CancellationTokenSource();

        var consuming = consumer.ConsumeAsync((_, _) => Task.CompletedTask, stop.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        consuming.IsCompleted.ShouldBeFalse("a consumer without a broker waits for it instead of giving up");
        await stop.CancelAsync();

        await consuming.WaitAsync(Eventually.DefaultGuard, TestContext.Current.CancellationToken);
    }

    internal static HealthCheckContext HealthContext() => new()
    {
        Registration = new HealthCheckRegistration(MessagingServiceCollectionExtensions.HealthCheckName, _ => null!, HealthStatus.Degraded, null),
    };

    private static RabbitMqConnection UnreachableConnection() => new(
        Options.Create(new MessagingOptions
        {
            Uri = new Uri($"amqp://elevator:secret-password@127.0.0.1:{ClosedPort()}/"),
            ConnectTimeout = TimeSpan.FromSeconds(2),
            PublishTimeout = TimeSpan.FromSeconds(2),
            RecoveryInterval = TimeSpan.FromMilliseconds(100),
        }),
        NullLogger<RabbitMqConnection>.Instance,
        TimeProvider.System);

    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>Stopping and restarting the broker container: confirmed commands survive, clients recover by themselves.</summary>
[Collection(RestartableBrokerTests.Name)]
public sealed class BrokerRestartTests(RestartableRabbitMqFixture broker)
{
    private static readonly TimeSpan RestartGuard = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task BrokerRestart_LosesNoConfirmedCommand_AndClientsRecover()
    {
        await broker.ResetAsync();
        var apiConnection = broker.CreateConnection();
        var publisher = new RabbitMqElevatorCommandPublisher(apiConnection);
        var engine = broker.CreateConnection();
        var health = new RabbitMqHealthCheck(engine);
        var inbox = new Inbox<ElevatorCommandV1>();
        using var stop = new CancellationTokenSource();
        var consuming = new RabbitMqElevatorCommandConsumer(engine, broker.Logger<RabbitMqElevatorCommandConsumer>()).ConsumeAsync(
            async (command, token) =>
            {
                // A slow engine: commands are still queued when the broker goes down.
                await Task.Delay(TimeSpan.FromMilliseconds(50), token);
                await inbox.Handle(command, token);
            },
            stop.Token);
        var beforeRestart = Enumerable.Range(1, 30).Select(index => TestMessages.Car((index % 10) + 1)).ToArray();
        foreach (var command in beforeRestart)
        {
            await publisher.PublishAsync(command, TestContext.Current.CancellationToken);
        }

        await broker.Container.StopAsync(TestContext.Current.CancellationToken);
        await Eventually.IsTrueAsync(
            async () => (await health.CheckHealthAsync(UnreachableBrokerTests.HealthContext(), TestContext.Current.CancellationToken)).Status == HealthStatus.Degraded,
            "the health check to report the lost broker");
        await Should.ThrowAsync<MessagingUnavailableException>(() => publisher.PublishAsync(TestMessages.Car(2), TestContext.Current.CancellationToken));
        await broker.Container.StartAsync(TestContext.Current.CancellationToken);
        await Eventually.IsTrueAsync(() => engine.IsConnected && apiConnection.IsConnected, "both clients to reconnect", RestartGuard);
        var afterRestart = TestMessages.Service(ServiceAction.ReturnToService);
        await Eventually.IsTrueAsync(
            async () =>
            {
                try
                {
                    await publisher.PublishAsync(afterRestart, TestContext.Current.CancellationToken);
                    return true;
                }
                catch (MessagingUnavailableException)
                {
                    return false;
                }
            },
            "a publish to succeed after the restart",
            RestartGuard);
        var received = await inbox.WaitForAsync(
            commands => beforeRestart.Append<ElevatorCommandV1>(afterRestart).All(commands.Contains),
            "every confirmed command",
            RestartGuard);
        await stop.CancelAsync();
        await consuming.WaitAsync(Eventually.DefaultGuard, TestContext.Current.CancellationToken);

        received.Distinct().Count().ShouldBe(beforeRestart.Length + 1, "at least once: a redelivered command may repeat, but none is missing");
        (await health.CheckHealthAsync(UnreachableBrokerTests.HealthContext(), TestContext.Current.CancellationToken)).Status.ShouldBe(HealthStatus.Healthy);
    }
}
