using System.Text;
using ElevatorSystem.Contracts;
using ElevatorSystem.Infrastructure.Messaging;
using RabbitMQ.Client;

namespace ElevatorSystem.Messaging.Tests;

/// <summary>The engine's updates fan out to every subscriber instance through a real broker.</summary>
[Collection(SharedBrokerTests.Name)]
public sealed class UpdateDeliveryTests(RabbitMqFixture broker) : IAsyncLifetime
{
    private readonly List<(CancellationTokenSource Stop, Task Subscription)> _subscriptions = [];

    public async ValueTask InitializeAsync() => await broker.ResetAsync();

    public async ValueTask DisposeAsync()
    {
        broker.ReportLogs();
        foreach (var (stop, subscription) in _subscriptions)
        {
            await stop.CancelAsync();
            await subscription.WaitAsync(Eventually.DefaultGuard, TestContext.Current.CancellationToken);
            stop.Dispose();
        }
    }

    [Fact]
    public async Task PublishAsync_BothUpdateTypes_ReachEverySubscriber()
    {
        var publisher = new RabbitMqElevatorUpdatePublisher(broker.CreateConnection());
        Subscriber[] subscribers = [Subscribe(broker.CreateConnection()), Subscribe(broker.CreateConnection())];
        await WaitUntilSubscribedAsync(publisher, subscribers);
        var elevatorUpdate = TestMessages.ElevatorUpdate(floor: 9);
        var occupancyUpdate = TestMessages.OccupancyUpdate();

        await publisher.PublishAsync(elevatorUpdate, TestContext.Current.CancellationToken);
        await publisher.PublishAsync(occupancyUpdate, TestContext.Current.CancellationToken);

        foreach (var subscriber in subscribers)
        {
            var elevator = await subscriber.Elevator.WaitForAsync(updates => updates.Any(update => update.UpdateId == elevatorUpdate.UpdateId), "the elevator update");
            var occupancy = await subscriber.Occupancy.WaitForAsync(updates => updates.Any(update => update.UpdateId == occupancyUpdate.UpdateId), "the occupancy update");
            elevator.Single(update => update.UpdateId == elevatorUpdate.UpdateId).Status.CurrentFloor.ShouldBe(9);
            occupancy.Single(update => update.UpdateId == occupancyUpdate.UpdateId).Occupancy.Floors.ShouldBe(occupancyUpdate.Occupancy.Floors);
        }
    }

    [Fact]
    public async Task PublishAsync_NoSubscriber_IsStillConfirmed()
    {
        var publisher = new RabbitMqElevatorUpdatePublisher(broker.CreateConnection());

        await publisher.PublishAsync(TestMessages.ElevatorUpdate(), TestContext.Current.CancellationToken);
        await publisher.PublishAsync(TestMessages.OccupancyUpdate(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SubscribeAsync_MalformedUpdate_IsDeadLetteredAndTheSubscriptionCarriesOn()
    {
        var publisher = new RabbitMqElevatorUpdatePublisher(broker.CreateConnection());
        var subscriber = Subscribe(broker.CreateConnection());
        await WaitUntilSubscribedAsync(publisher, [subscriber]);
        var (_, channel) = await broker.OpenRawChannelAsync();
        var messageId = Guid.CreateVersion7().ToString();

        await channel.BasicPublishAsync(
            RabbitMqTopology.UpdatesExchange,
            RabbitMqTopology.ElevatorUpdateRoutingKey,
            mandatory: false,
            new BasicProperties { MessageId = messageId, ContentType = "application/json" },
            Encoding.UTF8.GetBytes("""{"updateId":"not-a-guid"}"""),
            TestContext.Current.CancellationToken);
        var deadLetter = await broker.WaitForDeadLetterAsync(messageId);
        var after = TestMessages.ElevatorUpdate();
        await publisher.PublishAsync(after, TestContext.Current.CancellationToken);

        RabbitMqFixture.Header(deadLetter.BasicProperties, RabbitMqTopology.DeadLetterReasonHeader).ShouldBe("malformed");
        await subscriber.Elevator.WaitForAsync(updates => updates.Any(update => update.UpdateId == after.UpdateId), "the next valid update");
    }

    [Fact]
    public async Task SubscribeAsync_HandlerAlwaysThrows_UpdateIsDeadLetteredAfterBoundedRetries()
    {
        var publisher = new RabbitMqElevatorUpdatePublisher(broker.CreateConnection());
        var poison = TestMessages.ElevatorUpdate();
        var attempts = 0;
        var probes = new Inbox<ElevatorUpdateV1>();
        var stop = new CancellationTokenSource();
        var subscription = new RabbitMqElevatorUpdateSubscriber(broker.CreateConnection(), broker.Logger<RabbitMqElevatorUpdateSubscriber>()).SubscribeAsync(
            (update, token) =>
            {
                if (update.UpdateId != poison.UpdateId)
                {
                    return probes.Handle(update, token);
                }

                Interlocked.Increment(ref attempts);
                throw new InvalidOperationException("hub is down");
            },
            (_, _) => Task.CompletedTask,
            stop.Token);
        _subscriptions.Add((stop, subscription));
        await WaitUntilSubscribedAsync(publisher, probes);

        await publisher.PublishAsync(poison, TestContext.Current.CancellationToken);
        var deadLetter = await broker.WaitForDeadLetterAsync(poison.UpdateId.ToString());

        RabbitMqFixture.Header(deadLetter.BasicProperties, RabbitMqTopology.DeadLetterReasonHeader).ShouldBe("handler-failed");
        attempts.ShouldBe(RabbitMqElevatorUpdateSubscriber.MaxHandlerAttempts);
    }

    private Subscriber Subscribe(RabbitMqConnection connection)
    {
        var subscriber = new Subscriber();
        var stop = new CancellationTokenSource();
        var subscription = new RabbitMqElevatorUpdateSubscriber(connection, broker.Logger<RabbitMqElevatorUpdateSubscriber>())
            .SubscribeAsync(subscriber.Elevator.Handle, subscriber.Occupancy.Handle, stop.Token);
        _subscriptions.Add((stop, subscription));
        return subscriber;
    }

    /// <summary>A subscription's queue is declared asynchronously; publish probes until each subscriber receives one.</summary>
    private static Task WaitUntilSubscribedAsync(RabbitMqElevatorUpdatePublisher publisher, Subscriber[] subscribers) =>
        Eventually.IsTrueAsync(
            async () =>
            {
                await publisher.PublishAsync(TestMessages.ElevatorUpdate(), TestContext.Current.CancellationToken);
                return subscribers.All(subscriber => subscriber.Elevator.Messages.Count > 0);
            },
            "every subscriber to receive a probe update");

    private static Task WaitUntilSubscribedAsync(RabbitMqElevatorUpdatePublisher publisher, Inbox<ElevatorUpdateV1> probes) =>
        Eventually.IsTrueAsync(
            async () =>
            {
                await publisher.PublishAsync(TestMessages.ElevatorUpdate(), TestContext.Current.CancellationToken);
                return probes.Messages.Count > 0;
            },
            "the subscriber to receive a probe update");

    private sealed class Subscriber
    {
        public Inbox<ElevatorUpdateV1> Elevator { get; } = new();

        public Inbox<OccupancyUpdateV1> Occupancy { get; } = new();
    }
}
