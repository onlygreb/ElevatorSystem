using System.Diagnostics;
using System.Text;
using ElevatorSystem.Contracts;
using ElevatorSystem.Infrastructure.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ElevatorSystem.Messaging.Tests;

/// <summary>Commands from the API to the engine through a real broker: at-least-once, in order, one active engine.</summary>
[Collection(SharedBrokerTests.Name)]
public sealed class CommandDeliveryTests(RabbitMqFixture broker) : IAsyncLifetime
{
    private readonly List<(CancellationTokenSource Stop, Task Consuming)> _consumers = [];

    public async ValueTask InitializeAsync() => await broker.ResetAsync();

    public async ValueTask DisposeAsync()
    {
        broker.ReportLogs();
        foreach (var (stop, consuming) in _consumers)
        {
            await stop.CancelAsync();
            await consuming.WaitAsync(Eventually.DefaultGuard, TestContext.Current.CancellationToken);
            stop.Dispose();
        }
    }

    [Fact]
    public async Task PublishAsync_EveryCommandType_IsConsumedInOrder()
    {
        var publisher = new RabbitMqElevatorCommandPublisher(broker.CreateConnection());
        ElevatorCommandV1[] commands = [TestMessages.Hall(5, CallDirection.Down), TestMessages.Car(2), TestMessages.Service(ServiceAction.StartMaintenance)];
        var inbox = new Inbox<ElevatorCommandV1>();

        foreach (var command in commands)
        {
            await publisher.PublishAsync(command, TestContext.Current.CancellationToken);
        }

        StartConsumer(broker.CreateConnection(), inbox.Handle);
        var received = await inbox.WaitForCountAsync(commands.Length);

        received.ShouldBe(commands);
    }

    [Fact]
    public async Task ConsumeAsync_ConsumerDiesBeforeAcknowledging_CommandIsRedeliveredAndHandled()
    {
        var publisher = new RabbitMqElevatorCommandPublisher(broker.CreateConnection());
        var command = TestMessages.Car(7);
        var dyingName = $"dying-engine-{Guid.CreateVersion7():N}";
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hung = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StartConsumer(broker.CreateConnection(options => options.ClientName = dyingName), async (_, _) =>
        {
            // Hangs and ignores cancellation, like an engine that freezes and is then killed.
            handlerStarted.TrySetResult();
            await hung.Task;
        });
        try
        {
            await publisher.PublishAsync(command, TestContext.Current.CancellationToken);
            await handlerStarted.Task.WaitAsync(Eventually.DefaultGuard, TestContext.Current.CancellationToken);
            var standby = new Inbox<ElevatorCommandV1>();
            StartConsumer(broker.CreateConnection(), standby.Handle);
            await WaitForConsumersAsync(2);

            await broker.KillConnectionAsync(dyingName);
            var received = await standby.WaitForCountAsync(1);

            received.ShouldBe([command], "the unacknowledged command goes back to the queue and the standby engine takes over");
        }
        finally
        {
            hung.SetResult();
        }
    }

    [Fact]
    public async Task ConsumeAsync_ConsumerStopsBeforeAcknowledging_CommandIsRedeliveredToTheNextConsumer()
    {
        var publisher = new RabbitMqElevatorCommandPublisher(broker.CreateConnection());
        var command = TestMessages.Hall(4);
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (stop, consuming) = StartConsumer(broker.CreateConnection(), async (_, token) =>
        {
            handlerStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await publisher.PublishAsync(command, TestContext.Current.CancellationToken);
        await handlerStarted.Task.WaitAsync(Eventually.DefaultGuard, TestContext.Current.CancellationToken);

        await stop.CancelAsync();
        await consuming.WaitAsync(Eventually.DefaultGuard, TestContext.Current.CancellationToken);
        var next = new Inbox<ElevatorCommandV1>();
        StartConsumer(broker.CreateConnection(), next.Handle);
        var received = await next.WaitForCountAsync(1);

        received.ShouldBe([command]);
    }

    [Fact]
    public async Task ConsumeAsync_HandlerAlwaysThrows_CommandIsDeadLetteredAfterTheDeliveryLimit()
    {
        var publisher = new RabbitMqElevatorCommandPublisher(broker.CreateConnection());
        var command = TestMessages.Car(9);
        var attempts = 0;
        StartConsumer(broker.CreateConnection(), (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("engine bug");
        });

        await publisher.PublishAsync(command, TestContext.Current.CancellationToken);
        var deadLetter = await broker.WaitForDeadLetterAsync(command.CommandId.ToString());

        RabbitMqFixture.Header(deadLetter.BasicProperties, "x-first-death-reason").ShouldBe("delivery_limit");
        attempts.ShouldBe(RabbitMqTopology.DeliveryLimit + 1, "the first delivery plus one retry per allowed failure");
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"type":"open-sesame.v1","commandId":"0199f4a3-0000-7000-8000-000000000001","issuedAt":"2026-01-05T08:00:00Z"}""")]
    [InlineData("""{"type":"service.v1","commandId":"0199f4a3-0000-7000-8000-000000000002","issuedAt":"2026-01-05T08:00:00Z","action":99}""")]
    [InlineData("""{"type":"car-call.v1","commandId":"00000000-0000-0000-0000-000000000000","issuedAt":"2026-01-05T08:00:00Z","floor":3}""")]
    [InlineData("""{"type":"passenger.v1","commandId":"0199f4a3-0000-7000-8000-000000000003","issuedAt":"2026-01-05T08:00:00Z","passengerId":"00000000-0000-0000-0000-000000000000","action":"Join"}""")]
    [InlineData("""{"type":"passenger.v1","commandId":"0199f4a3-0000-7000-8000-000000000004","issuedAt":"2026-01-05T08:00:00Z","passengerId":"8d6f2c1e-5b3a-4c7d-9e8f-0a1b2c3d4e5f","action":42}""")]
    [InlineData("""{"type":"scenario.v1","commandId":"0199f4a3-0000-7000-8000-000000000005","issuedAt":"2026-01-05T08:00:00Z","scenario":17}""")]
    [InlineData("""{"type":"scheduling.v1","commandId":"0199f4a3-0000-7000-8000-000000000006","issuedAt":"2026-01-05T08:00:00Z","scheduling":5}""")]
    public async Task ConsumeAsync_MalformedMessage_GoesStraightToTheDeadLetterQueue(string body)
    {
        var handled = new Inbox<ElevatorCommandV1>();
        StartConsumer(broker.CreateConnection(), handled.Handle);
        var messageId = Guid.CreateVersion7().ToString();
        var (_, channel) = await broker.OpenRawChannelAsync();

        await channel.BasicPublishAsync(
            RabbitMqTopology.CommandsExchange,
            RabbitMqTopology.CommandRoutingKey,
            mandatory: true,
            new BasicProperties { MessageId = messageId, ContentType = "application/json", Persistent = true },
            Encoding.UTF8.GetBytes(body),
            TestContext.Current.CancellationToken);
        var deadLetter = await broker.WaitForDeadLetterAsync(messageId);

        RabbitMqFixture.Header(deadLetter.BasicProperties, "x-first-death-reason").ShouldBe("rejected", "rejected at once, not retried up to the delivery limit");
        handled.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task PassengerScenarioAndSchedulingCommands_RoundTripThroughTheBroker()
    {
        var publisher = new RabbitMqElevatorCommandPublisher(broker.CreateConnection());
        var received = new Inbox<ElevatorCommandV1>();
        StartConsumer(broker.CreateConnection(), received.Handle);
        var passenger = new PassengerCommandV1(Guid.CreateVersion7(), DateTimeOffset.UnixEpoch, Guid.NewGuid(), PassengerAction.ChooseFloor, Floor: 7);
        var scenario = new ScenarioCommandV1(Guid.CreateVersion7(), DateTimeOffset.UnixEpoch, DemoScenario.LunchBreak);
        var stop = new ScenarioCommandV1(Guid.CreateVersion7(), DateTimeOffset.UnixEpoch, null);
        var scheduling = new SchedulingCommandV1(Guid.CreateVersion7(), DateTimeOffset.UnixEpoch, Scheduling.Look);

        await publisher.PublishAsync(passenger, TestContext.Current.CancellationToken);
        await publisher.PublishAsync(scenario, TestContext.Current.CancellationToken);
        await publisher.PublishAsync(stop, TestContext.Current.CancellationToken);
        await publisher.PublishAsync(scheduling, TestContext.Current.CancellationToken);

        (await received.WaitForCountAsync(4)).ShouldBe([passenger, scenario, stop, scheduling]);
    }

    [Fact]
    public async Task ConsumeAsync_TwoEngines_OnlyOneReceivesCommandsAndTheOtherTakesOverWhenItStops()
    {
        var publisher = new RabbitMqElevatorCommandPublisher(broker.CreateConnection());
        var active = new Inbox<ElevatorCommandV1>();
        var (stopActive, activeConsuming) = StartConsumer(broker.CreateConnection(), active.Handle);
        var probe = TestMessages.Car(1);
        await publisher.PublishAsync(probe, TestContext.Current.CancellationToken);
        await active.WaitForCountAsync(1);
        var standby = new Inbox<ElevatorCommandV1>();
        StartConsumer(broker.CreateConnection(), standby.Handle);
        await WaitForConsumersAsync(2);

        for (var floor = 2; floor <= 6; floor++)
        {
            await publisher.PublishAsync(TestMessages.Car(floor), TestContext.Current.CancellationToken);
        }

        await active.WaitForCountAsync(6);
        await stopActive.CancelAsync();
        await activeConsuming;
        var afterTakeover = TestMessages.Car(7);
        await publisher.PublishAsync(afterTakeover, TestContext.Current.CancellationToken);
        var received = await standby.WaitForCountAsync(1);

        received.ShouldBe([afterTakeover], "the standby engine received nothing while the first one was consuming");
    }

    [Fact]
    public async Task PublishAsync_TraceContext_CrossesTheBroker()
    {
        using var testSource = new ActivitySource("ElevatorSystem.Messaging.Tests");
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source == testSource || source.Name == RabbitMqTelemetry.ActivitySourceName,
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        var publisher = new RabbitMqElevatorCommandPublisher(broker.CreateConnection());
        var traces = new Inbox<(ActivityTraceId TraceId, ActivityKind Kind)>();
        StartConsumer(broker.CreateConnection(), (_, token) => traces.Handle((Activity.Current!.TraceId, Activity.Current.Kind), token));

        ActivityTraceId requestTrace;
        using (var request = testSource.StartActivity("POST /api/v1/car-calls", ActivityKind.Server))
        {
            requestTrace = request!.TraceId;
            await publisher.PublishAsync(TestMessages.Car(8), TestContext.Current.CancellationToken);
        }

        var received = await traces.WaitForCountAsync(1);

        received.ShouldBe([(requestTrace, ActivityKind.Consumer)], "the handler runs in a consumer span of the publisher's trace");
    }

    [Fact]
    public async Task PublishAsync_SetsTheEnvelopeHeaders()
    {
        var publisher = new RabbitMqElevatorCommandPublisher(broker.CreateConnection());
        var command = TestMessages.Hall(3, CallDirection.Down);
        var (_, channel) = await broker.OpenRawChannelAsync();
        var delivered = new TaskCompletionSource<(IReadOnlyBasicProperties Properties, string Body)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) =>
        {
            delivered.TrySetResult((delivery.BasicProperties, Encoding.UTF8.GetString(delivery.Body.Span)));
            return Task.CompletedTask;
        };
        // The commands queue has a single active consumer, which rules out basic.get: read it as a consumer.
        await channel.BasicConsumeAsync(RabbitMqTopology.CommandsQueue, autoAck: true, consumer, TestContext.Current.CancellationToken);

        await publisher.PublishAsync(command, TestContext.Current.CancellationToken);
        var (properties, body) = await delivered.Task.WaitAsync(Eventually.DefaultGuard, TestContext.Current.CancellationToken);
        await channel.CloseAsync(TestContext.Current.CancellationToken);

        properties.MessageId.ShouldBe(command.CommandId.ToString());
        properties.CorrelationId.ShouldNotBeNullOrEmpty();
        properties.ContentType.ShouldBe("application/json");
        properties.Type.ShouldBe("hall-call.v1");
        properties.Persistent.ShouldBeTrue();
        body.ShouldContain("\"type\":\"hall-call.v1\"");
    }

    [Fact]
    public async Task Logs_NameMessagesByIdAndType_NeverByContent()
    {
        var publisher = new RabbitMqElevatorCommandPublisher(broker.CreateConnection());
        var command = TestMessages.Hall(6, CallDirection.Up);
        var handled = new Inbox<ElevatorCommandV1>();
        var failures = 0;
        StartConsumer(broker.CreateConnection(), (received, token) =>
            Interlocked.Increment(ref failures) == 1 ? throw new InvalidOperationException("transient") : handled.Handle(received, token));
        var (_, channel) = await broker.OpenRawChannelAsync();
        const string Secret = "maria.silva@example.com";

        await publisher.PublishAsync(command, TestContext.Current.CancellationToken);
        await handled.WaitForCountAsync(1);
        await channel.BasicPublishAsync(
            RabbitMqTopology.CommandsExchange,
            RabbitMqTopology.CommandRoutingKey,
            mandatory: true,
            new BasicProperties { MessageId = "poison-with-personal-data", ContentType = "application/json" },
            Encoding.UTF8.GetBytes($$"""{"type":"car-call.v1","note":"{{Secret}}" """),
            TestContext.Current.CancellationToken);
        await broker.WaitForDeadLetterAsync("poison-with-personal-data");

        var logs = broker.Logs.GetSnapshot().Select(record => record.Message).ToArray();
        logs.ShouldContain(line => line.Contains(command.CommandId.ToString()) && line.Contains("hall-call.v1"));
        logs.ShouldContain(line => line.Contains("poison-with-personal-data"));
        logs.ShouldAllBe(line => !line.Contains(Secret) && !line.Contains("\"floor\""));
    }

    private (CancellationTokenSource Stop, Task Consuming) StartConsumer(RabbitMqConnection connection, Func<ElevatorCommandV1, CancellationToken, Task> handle)
    {
        var stop = new CancellationTokenSource();
        var consumer = new RabbitMqElevatorCommandConsumer(connection, broker.Logger<RabbitMqElevatorCommandConsumer>());
        var consuming = consumer.ConsumeAsync(handle, stop.Token);
        _consumers.Add((stop, consuming));
        return (stop, consuming);
    }

    /// <summary>The broker counts only the active consumer of a single-active-consumer queue; ask it for all of them.</summary>
    private Task WaitForConsumersAsync(int count) =>
        Eventually.IsTrueAsync(
            async () => await broker.CountConsumersAsync(RabbitMqTopology.CommandsQueue) >= count,
            $"{count} consumers on {RabbitMqTopology.CommandsQueue}");
}
