using ElevatorSystem.Application.Hosting;
using ElevatorSystem.Application.InMemory.Persistence;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace ElevatorSystem.Application.Tests.Hosting;

/// <summary>
/// Outages of the database or the broker never stop the car (ADR-0007), and what clients are shown is
/// always something the engine can recover.
/// </summary>
public sealed class ElevatorEngineFailSafeTests
{
    [Fact]
    public async Task EveryPublishedStatus_IsSavedBeforeItIsPublished()
    {
        var store = new InMemoryElevatorStateStore();
        await using var harness = new EngineHarness { StateStore = store };
        var checking = new StoreCheckingPublisher(harness.Bus, store);
        harness.UpdatePublisher = checking;
        await harness.StartLeaderAsync();

        await harness.SendAsync(EngineHarness.CarCall(6));
        await harness.SendAsync(EngineHarness.HallCall(3, CallDirection.Up));
        await harness.Updates.WaitForAsync(update => update.Activity.Kind == ElevatorActivityKind.CallServed && update.Activity.Floor == 3);

        checking.Checked.ShouldBeGreaterThan(5);
        checking.Violations.ShouldBeEmpty("a status shown to clients must survive a crash, so the restored version stays above it");
    }

    [Fact]
    public async Task SlowSave_HoldsTheUpdateBack_UntilTheStateIsStored()
    {
        var store = new GatedStateStore();
        await using var harness = new EngineHarness { StateStore = store };
        var engine = harness.Start();
        await engine.WaitForRoleAsync(EngineRole.Leader);
        await Eventually(() => store.SaveAttempts > 0, "the first save to start");

        var publishedWhileSaving = harness.Updates.Count;
        store.Open();
        var opened = await harness.Updates.WaitForAsync(update => update.Activity.Kind == ElevatorActivityKind.DoorsOpened);

        publishedWhileSaving.ShouldBe(0);
        var stored = await store.LoadAsync(TestContext.Current.CancellationToken);
        stored.ShouldNotBeNull().Version.ShouldBeGreaterThanOrEqualTo(opened.Status.Version);
    }

    [Fact]
    public async Task DatabaseDown_TheCarKeepsServing_AndTheStateIsSavedOnceItIsBack()
    {
        var store = new FlakyStateStore();
        await using var harness = new EngineHarness { StateStore = store };
        var engine = await harness.StartLeaderAsync();
        store.IsDown = true;

        await harness.SendAsync(EngineHarness.CarCall(4));
        var served = await harness.Updates.WaitForAsync(update => update.Activity.Kind == ElevatorActivityKind.CallServed && update.Activity.Floor == 4);
        store.FailedSaves.ShouldBeGreaterThan(0);
        harness.Logs.Contains(2010).ShouldBeTrue("the outage is reported");
        store.IsDown = false;

        await Eventually(async () => (await store.LoadAsync(CancellationToken.None))?.Version >= served.Status.Version, "the state to be saved after the outage");
        await Eventually(() => harness.Logs.Contains(2011), "the recovery to be reported");
        engine.Engine.Role.ShouldBe(EngineRole.Leader);
    }

    [Fact]
    public async Task BrokerDown_TheCarKeepsServing_AndSubscribersCatchUpOnceItIsBack()
    {
        await using var harness = new EngineHarness
        {
            Options = new ElevatorEngineOptions { StatusRefreshInterval = TimeSpan.FromMilliseconds(100), StoreRetryInterval = TimeSpan.FromMilliseconds(50) },
        };
        var publisher = new FlakyUpdatePublisher(harness.Bus);
        harness.UpdatePublisher = publisher;
        var engine = await harness.StartLeaderAsync();
        publisher.IsDown = true;
        var from = harness.Updates.Count;

        await harness.SendAsync(EngineHarness.CarCall(3));
        var elevator = engine.Engine.Controller.ShouldNotBeNull().Elevator;
        await Eventually(() => elevator.Snapshot() is { CurrentFloor.Number: 3, State: ElevatorState.Idle }, "the car to serve floor 3 without the broker");
        publisher.Refused.ShouldBeGreaterThan(0);
        publisher.IsDown = false;

        var caughtUp = await harness.Updates.WaitForAsync(update => update.Status.CurrentFloor == 3 && update.Status.Status == ElevatorStatus.Idle, from);
        caughtUp.Status.Version.ShouldBe(elevator.Snapshot().Version, "the refresh re-sends the latest state");
        harness.Logs.Contains(2012).ShouldBeTrue();
        await Eventually(() => harness.Logs.Contains(2013), "the recovery to be reported");
    }

    [Fact]
    public async Task Idle_TheLatestUpdateIsResent_WithItsOriginalId()
    {
        await using var harness = new EngineHarness
        {
            Options = new ElevatorEngineOptions { StatusRefreshInterval = TimeSpan.FromMilliseconds(100) },
        };
        await harness.StartLeaderAsync();
        await harness.Updates.WaitForAsync(update => update.Activity.Kind == ElevatorActivityKind.DoorsClosed);
        var latest = harness.Updates.Messages[^1];

        var copy = await harness.Updates.WaitForAsync(update => update.UpdateId == latest.UpdateId, harness.Updates.Count);

        copy.ShouldBe(latest, "a late subscriber learns the current state; others drop it by id");
    }

    [Fact]
    public async Task DeduplicationStoreDown_TheCommandIsAppliedAnyway()
    {
        await using var harness = new EngineHarness { ProcessedCommands = new UnreachableProcessedCommandStore() };
        await harness.StartLeaderAsync();

        await harness.SendAsync(EngineHarness.CarCall(5));

        await harness.Updates.WaitForAsync(update => update.Activity.Kind == ElevatorActivityKind.CallServed && update.Activity.Floor == 5);
        harness.Logs.Contains(2016).ShouldBeTrue("applying without the duplicate check is reported");
    }

    [Fact]
    public async Task DatabaseDownWhenTakingOver_TheCarWaitsForTheLastState()
    {
        var store = new FlakyStateStore { IsDown = true };
        await using var harness = new EngineHarness { StateStore = store };
        var engine = harness.Start();
        await engine.WaitForRoleAsync(EngineRole.Standby);
        await Eventually(() => harness.Logs.Contains(2006), "the failed load to be reported");

        engine.Engine.Controller.ShouldBeNull("without the last state the car could hold calls nobody would serve");
        store.IsDown = false;

        await engine.WaitForRoleAsync(EngineRole.Leader);
        await harness.Updates.WaitForAsync(update => update.Activity.Kind == ElevatorActivityKind.DoorsOpened);
    }

    [Fact]
    public async Task Logs_NameCommandsByIdAndType_Only()
    {
        await using var harness = new EngineHarness { ProcessedCommands = new UnreachableProcessedCommandStore() };
        await harness.StartLeaderAsync();
        var command = EngineHarness.HallCall(7, CallDirection.Down);

        await harness.SendAsync(command);
        await harness.Updates.WaitForAsync(update => update.Activity.Kind == ElevatorActivityKind.CallServed && update.Activity.Floor == 7);

        var engineEntries = harness.Logs.Entries.Where(entry => entry.EventId.Id is >= 2000 and < 2100).ToList();
        engineEntries.ShouldContain(entry => entry.Message.Contains(command.CommandId.ToString(), StringComparison.Ordinal));
        engineEntries.ShouldNotContain(entry => entry.Message.Contains("\"floor\"", StringComparison.OrdinalIgnoreCase), "message bodies are never logged");
    }

    [Fact]
    public async Task Persister_ABurstOfChanges_IsSavedAsTheLatestStateOnly()
    {
        var elevator = new Elevator(Building.Default, TimeProvider.System);
        var store = new GatedStateStore();
        var persister = new SnapshotPersister(elevator, store, -1, new ElevatorEngineOptions(), TimeProvider.System, NullLogger.Instance);
        using var stop = new CancellationTokenSource();
        var running = persister.RunAsync(stop.Token);

        persister.Notify();
        await Eventually(() => store.SaveAttempts == 1, "the first save to start");
        for (var floor = 2; floor <= 10; floor++)
        {
            elevator.AddRequest(new PassengerRequest(RequestId.New(TimeProvider.System), new CarCall(new Floor(floor)), DateTimeOffset.UnixEpoch));
            persister.Notify();
        }

        store.Open();
        await persister.WaitUntilSavedAsync(elevator.Snapshot().Version, TestContext.Current.CancellationToken);

        persister.SavedVersion.ShouldBe(elevator.Snapshot().Version);
        store.SaveAttempts.ShouldBe(2, "nine changes during one save collapse into a single save of the newest state");
        await stop.CancelAsync();
        await running;
    }

    [Fact]
    public async Task Forwarder_BrokerThatNeverAnswers_DropsTheOldestUpdatesAndCountsThem()
    {
        var elevator = new Elevator(Building.Default, TimeProvider.System);
        var options = new ElevatorEngineOptions { UpdateBufferCapacity = 4 };
        var persister = new SnapshotPersister(elevator, new InMemoryElevatorStateStore(), 0, options, TimeProvider.System, NullLogger.Instance);
        var logs = new RecordingLoggerFactory();
        var forwarder = new ElevatorUpdateForwarder(new HangingPublisher(), persister, options, TimeProvider.System, logs.CreateLogger("forwarder"));
        using var stop = new CancellationTokenSource();
        var running = forwarder.RunAsync(stop.Token);
        var update = new ElevatorUpdateV1(Guid.CreateVersion7(), new ElevatorActivityDto(DateTimeOffset.UnixEpoch, ElevatorActivityKind.DoorsClosed, 1), ElevatorContractMapper.ToStatus(elevator.Snapshot()));

        for (var i = 0; i < 20; i++)
        {
            forwarder.Enqueue(update with { UpdateId = Guid.CreateVersion7() });
        }

        await Eventually(() => forwarder.DroppedCount >= 15, "the overflow to be dropped");
        logs.Contains(2014).ShouldBeTrue("drops are reported");
        await stop.CancelAsync();
        await running;
    }

    private static Task Eventually(Func<bool> condition, string because) => Eventually(() => Task.FromResult(condition()), because);

    private static async Task Eventually(Func<Task<bool>> condition, string because)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition())
        {
            if (guard.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for {because}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), CancellationToken.None);
        }
    }

    /// <summary>Checks, at the moment of publishing, that the store already holds the version being published.</summary>
    private sealed class StoreCheckingPublisher(IElevatorUpdatePublisher inner, IElevatorStateStore store) : IElevatorUpdatePublisher
    {
        private readonly List<string> _violations = [];
        private int _checked;

        public int Checked => Volatile.Read(ref _checked);

        public IReadOnlyList<string> Violations
        {
            get
            {
                lock (_violations)
                {
                    return [.. _violations];
                }
            }
        }

        public async Task PublishAsync(ElevatorUpdateV1 update, CancellationToken cancellationToken)
        {
            var stored = await store.LoadAsync(cancellationToken);
            if (stored is null || stored.Version < update.Status.Version)
            {
                lock (_violations)
                {
                    _violations.Add($"{update.Activity.Kind} at version {update.Status.Version}, stored {stored?.Version}");
                }
            }

            Interlocked.Increment(ref _checked);
            await inner.PublishAsync(update, cancellationToken);
        }

        public Task PublishAsync(OccupancyUpdateV1 update, CancellationToken cancellationToken) => inner.PublishAsync(update, cancellationToken);
    }

    private sealed class HangingPublisher : IElevatorUpdatePublisher
    {
        public Task PublishAsync(ElevatorUpdateV1 update, CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        public Task PublishAsync(OccupancyUpdateV1 update, CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
