using System.Diagnostics;
using ElevatorSystem.Application.InMemory.Messaging;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Contracts;
using Microsoft.Extensions.Logging;

namespace ElevatorSystem.Application.Tests.InMemory.Messaging;

/// <summary>The in-memory bus keeps the broker's delivery semantics inside one process (ADR-0018).</summary>
public sealed class InMemoryMessageBusTests
{
    private static readonly DateTimeOffset IssuedAt = new(2026, 1, 5, 8, 0, 0, TimeSpan.Zero);
    private readonly RecordingLogger<InMemoryMessageBus> _logger = new();

    [Fact]
    public async Task PublishAsync_EveryCommandType_IsConsumedInOrder()
    {
        var bus = CreateBus();
        ElevatorCommandV1[] commands = [Hall(5), Car(2), Service(ServiceAction.EmergencyStop)];
        var inbox = new MessageInbox<ElevatorCommandV1>();
        using var stop = new CancellationTokenSource();

        foreach (var command in commands)
        {
            await bus.PublishAsync(command, TestContext.Current.CancellationToken);
        }

        var consuming = bus.ConsumeAsync(inbox.Handle, stop.Token);
        var received = await inbox.WaitForCountAsync(commands.Length);
        await stop.CancelAsync();
        await consuming;

        received.ShouldBe(commands);
    }

    [Fact]
    public async Task ConsumeAsync_HandlerFailsOnce_IsRetriedAndHandled()
    {
        var bus = CreateBus();
        var inbox = new MessageInbox<ElevatorCommandV1>();
        var attempts = 0;
        using var stop = new CancellationTokenSource();
        var consuming = bus.ConsumeAsync(
            (command, token) => ++attempts == 1 ? throw new InvalidOperationException("transient") : inbox.Handle(command, token),
            stop.Token);

        await bus.PublishAsync(Car(4), TestContext.Current.CancellationToken);
        await inbox.WaitForCountAsync(1);
        await stop.CancelAsync();
        await consuming;

        attempts.ShouldBe(2);
    }

    [Fact]
    public async Task ConsumeAsync_HandlerAlwaysThrows_DropsAfterMaxAttemptsAndCarriesOn()
    {
        var bus = CreateBus(maxDeliveryAttempts: 3);
        var poison = Car(9);
        var next = Car(3);
        var inbox = new MessageInbox<ElevatorCommandV1>();
        var poisonAttempts = 0;
        using var stop = new CancellationTokenSource();
        var consuming = bus.ConsumeAsync(
            (command, token) =>
            {
                if (command == poison)
                {
                    poisonAttempts++;
                    throw new InvalidOperationException("poison");
                }

                return inbox.Handle(command, token);
            },
            stop.Token);

        await bus.PublishAsync(poison, TestContext.Current.CancellationToken);
        await bus.PublishAsync(next, TestContext.Current.CancellationToken);
        var received = await inbox.WaitForCountAsync(1);
        await stop.CancelAsync();
        await consuming;

        poisonAttempts.ShouldBe(3, "the handler is retried up to the delivery limit, then the message is dropped");
        received.ShouldBe([next]);
        _logger.Entries.ShouldContain(entry => entry.Level == LogLevel.Error && entry.Message.Contains(poison.CommandId.ToString()));
    }

    [Fact]
    public async Task ConsumeAsync_TwoConsumers_OnlyOneReceivesUntilItStops()
    {
        var bus = CreateBus();
        var first = new MessageInbox<ElevatorCommandV1>();
        var second = new MessageInbox<ElevatorCommandV1>();
        using var stopFirst = new CancellationTokenSource();
        using var stopSecond = new CancellationTokenSource();
        var firstConsuming = bus.ConsumeAsync(first.Handle, stopFirst.Token);
        var secondConsuming = bus.ConsumeAsync(second.Handle, stopSecond.Token);

        for (var floor = 1; floor <= 5; floor++)
        {
            await bus.PublishAsync(Car(floor), TestContext.Current.CancellationToken);
        }

        await first.WaitForCountAsync(5);
        await stopFirst.CancelAsync();
        await firstConsuming;
        var takenOver = Car(6);
        await bus.PublishAsync(takenOver, TestContext.Current.CancellationToken);
        var afterTakeover = await second.WaitForCountAsync(1);
        await stopSecond.CancelAsync();
        await secondConsuming;

        afterTakeover.ShouldBe([takenOver], "the standby consumer received nothing while the first was active");
        first.Messages.Count.ShouldBe(5);
    }

    [Fact]
    public async Task ConsumeAsync_ConsumerStopsBeforeHandlerCompletes_CommandIsRedeliveredToTheNext()
    {
        var bus = CreateBus();
        var command = Hall(7, CallDirection.Down);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopFirst = new CancellationTokenSource();
        var firstConsuming = bus.ConsumeAsync(
            async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, token);
            },
            stopFirst.Token);
        await bus.PublishAsync(command, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await stopFirst.CancelAsync();
        await firstConsuming;
        var second = new MessageInbox<ElevatorCommandV1>();
        using var stopSecond = new CancellationTokenSource();
        var secondConsuming = bus.ConsumeAsync(second.Handle, stopSecond.Token);
        var received = await second.WaitForCountAsync(1);
        await stopSecond.CancelAsync();
        await secondConsuming;

        received.ShouldBe([command]);
    }

    [Fact]
    public async Task PublishAsync_CommandQueueFull_ThrowsMessagingUnavailable()
    {
        var bus = CreateBus(commandCapacity: 2);
        await bus.PublishAsync(Car(1), TestContext.Current.CancellationToken);
        await bus.PublishAsync(Car(2), TestContext.Current.CancellationToken);

        await Should.ThrowAsync<MessagingUnavailableException>(() => bus.PublishAsync(Car(3), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConsumeAsync_CancelledWhileWaitingToBecomeActive_ReturnsWithoutError()
    {
        var bus = CreateBus();
        using var stopFirst = new CancellationTokenSource();
        using var stopSecond = new CancellationTokenSource();
        var firstConsuming = bus.ConsumeAsync((_, _) => Task.CompletedTask, stopFirst.Token);
        var secondConsuming = bus.ConsumeAsync((_, _) => Task.CompletedTask, stopSecond.Token);

        await stopSecond.CancelAsync();
        await secondConsuming;
        await stopFirst.CancelAsync();
        await firstConsuming;

        secondConsuming.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task PublishAsync_Updates_ReachEverySubscriber()
    {
        var bus = CreateBus();
        var subscribers = Enumerable.Range(0, 2).Select(_ => new Subscriber()).ToArray();
        using var stop = new CancellationTokenSource();
        var subscriptions = subscribers.Select(subscriber => subscriber.SubscribeTo(bus, stop.Token)).ToArray();
        var elevatorUpdate = ElevatorUpdate();
        var occupancyUpdate = OccupancyUpdate();

        await bus.PublishAsync(elevatorUpdate, TestContext.Current.CancellationToken);
        await bus.PublishAsync(occupancyUpdate, TestContext.Current.CancellationToken);
        foreach (var subscriber in subscribers)
        {
            await subscriber.Elevator.WaitForCountAsync(1);
            await subscriber.Occupancy.WaitForCountAsync(1);
        }

        await stop.CancelAsync();
        await Task.WhenAll(subscriptions);

        subscribers.ShouldAllBe(subscriber =>
            subscriber.Elevator.Messages.Single() == elevatorUpdate && subscriber.Occupancy.Messages.Single() == occupancyUpdate);
    }

    [Fact]
    public async Task SubscribeAsync_HandlerAlwaysThrows_OtherSubscribersAndLaterUpdatesAreUnaffected()
    {
        var bus = CreateBus(maxDeliveryAttempts: 2);
        var healthy = new Subscriber();
        var failingAttempts = 0;
        using var stop = new CancellationTokenSource();
        var failing = bus.SubscribeAsync(
            (_, _) =>
            {
                Interlocked.Increment(ref failingAttempts);
                throw new InvalidOperationException("hub is down");
            },
            (_, _) => Task.CompletedTask,
            stop.Token);
        var healthySubscription = healthy.SubscribeTo(bus, stop.Token);

        await bus.PublishAsync(ElevatorUpdate(), TestContext.Current.CancellationToken);
        await bus.PublishAsync(ElevatorUpdate(), TestContext.Current.CancellationToken);
        await healthy.Elevator.WaitForCountAsync(2);
        await stop.CancelAsync();
        await Task.WhenAll(failing, healthySubscription);

        failingAttempts.ShouldBe(4, "each of the two updates is tried twice and then dropped");
    }

    [Fact]
    public async Task SubscribeAsync_SubscriberFallsBehind_LosesItsOldestUpdatesFirst()
    {
        var bus = CreateBus(subscriberCapacity: 2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inbox = new MessageInbox<ElevatorUpdateV1>();
        using var stop = new CancellationTokenSource();
        var subscription = bus.SubscribeAsync(
            async (update, token) =>
            {
                handlerEntered.TrySetResult();
                await release.Task.WaitAsync(token);
                inbox.Add(update);
            },
            (_, _) => Task.CompletedTask,
            stop.Token);
        var updates = Enumerable.Range(0, 5).Select(_ => ElevatorUpdate()).ToArray();

        await bus.PublishAsync(updates[0], TestContext.Current.CancellationToken);
        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        foreach (var update in updates[1..])
        {
            await bus.PublishAsync(update, TestContext.Current.CancellationToken);
        }

        release.SetResult();
        var received = await inbox.WaitForCountAsync(3);
        await stop.CancelAsync();
        await subscription;

        received.ShouldBe([updates[0], updates[3], updates[4]], "the inbox holds two updates and drops the oldest when full");
    }

    [Fact]
    public async Task PublishAsync_TraceContext_ContinuesInTheHandler()
    {
        using var listener = ListenToMessaging();
        var bus = CreateBus();
        var inbox = new MessageInbox<ActivityTraceId>();
        using var stop = new CancellationTokenSource();
        var consuming = bus.ConsumeAsync((_, token) => inbox.Handle(Activity.Current!.TraceId, token), stop.Token);
        using var source = new ActivitySource("ElevatorSystem.Tests");
        using var listenToTests = new ActivityListener
        {
            ShouldListenTo = candidate => candidate == source,
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listenToTests);

        ActivityTraceId traceId;
        using (var request = source.StartActivity("POST /api/v1/car-calls"))
        {
            traceId = request!.TraceId;
            await bus.PublishAsync(Car(8), TestContext.Current.CancellationToken);
        }

        var received = await inbox.WaitForCountAsync(1);
        await stop.CancelAsync();
        await consuming;

        received.ShouldBe([traceId]);
    }

    [Fact]
    public async Task Logs_NameMessagesByIdAndType_NeverByContent()
    {
        var bus = CreateBus(maxDeliveryAttempts: 1);
        var command = Hall(6, CallDirection.Up);
        using var stop = new CancellationTokenSource();
        var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consuming = bus.ConsumeAsync(
            (_, _) =>
            {
                dropped.TrySetResult();
                throw new InvalidOperationException("boom");
            },
            stop.Token);

        await bus.PublishAsync(command, TestContext.Current.CancellationToken);
        await dropped.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await stop.CancelAsync();
        await consuming;

        _logger.Entries.ShouldNotBeEmpty();
        _logger.Entries.ShouldAllBe(entry => !entry.Message.Contains("\"floor\"") && !entry.Message.Contains("Floor ="));
        _logger.Entries.ShouldContain(entry => entry.Message.Contains("hall-call.v1") && entry.Message.Contains(command.CommandId.ToString()));
    }

    private static ActivityListener ListenToMessaging()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "ElevatorSystem.Messaging",
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private InMemoryMessageBus CreateBus(
        int maxDeliveryAttempts = InMemoryMessageBus.DefaultMaxDeliveryAttempts,
        int commandCapacity = InMemoryMessageBus.DefaultCommandCapacity,
        int subscriberCapacity = InMemoryMessageBus.DefaultSubscriberCapacity) =>
        new(_logger, maxDeliveryAttempts, commandCapacity, subscriberCapacity);

    private static HallCallCommandV1 Hall(int floor, CallDirection direction = CallDirection.Up) =>
        new(Guid.CreateVersion7(), IssuedAt, floor, direction);

    private static CarCallCommandV1 Car(int floor) => new(Guid.CreateVersion7(), IssuedAt, floor);

    private static ServiceCommandV1 Service(ServiceAction action) => new(Guid.CreateVersion7(), IssuedAt, action);

    private static ElevatorUpdateV1 ElevatorUpdate() => new(
        Guid.CreateVersion7(),
        new ElevatorActivityDto(IssuedAt, ElevatorActivityKind.DoorsOpened, 3),
        new ElevatorStatusDto(3, ElevatorStatus.DoorOpen, null, null, [], [], 1, IssuedAt));

    private static OccupancyUpdateV1 OccupancyUpdate() => new(
        Guid.CreateVersion7(),
        new BuildingOccupancyDto(1, 8, [new FloorOccupancyDto(3, 0, 1)], 0, null, IssuedAt));

    private sealed class Subscriber
    {
        public MessageInbox<ElevatorUpdateV1> Elevator { get; } = new();

        public MessageInbox<OccupancyUpdateV1> Occupancy { get; } = new();

        public Task SubscribeTo(InMemoryMessageBus bus, CancellationToken cancellationToken) =>
            bus.SubscribeAsync(Elevator.Handle, Occupancy.Handle, cancellationToken);
    }
}
