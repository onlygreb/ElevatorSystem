using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Services;
using Microsoft.Extensions.Time.Testing;

namespace ElevatorSystem.Web.Tests.Services;

public sealed class ElevatorStateStoreTests
{
    [Fact]
    public void ApplyStatus_NewerVersion_ReplacesTheShownStatus()
    {
        var store = ConnectedStoreShowing(TestData.Status(floor: 3, version: 5));

        var applied = store.ApplyStatus(TestData.Status(floor: 4, version: 6));

        applied.ShouldBeTrue();
        store.Status!.CurrentFloor.ShouldBe(4);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void ApplyStatus_OlderOrSameVersion_IsIgnored(long staleVersion)
    {
        var store = ConnectedStoreShowing(TestData.Status(floor: 3, version: 5));

        var applied = store.ApplyStatus(TestData.Status(floor: 9, version: staleVersion));

        applied.ShouldBeFalse();
        store.Status!.CurrentFloor.ShouldBe(3, "a late or duplicated status must never move the car backwards");
    }

    [Fact]
    public void ApplyStatus_SameVersionTakenLater_IsApplied()
    {
        var store = ConnectedStoreShowing(TestData.Status(version: 5));

        var applied = store.ApplyStatus(TestData.Status(version: 5) with { TakenAt = TestData.Noon.AddSeconds(1), Speed = 8 });

        applied.ShouldBeTrue("the speed changes without a new version (ADR-0031)");
        store.Status!.Speed.ShouldBe(8);
    }

    [Fact]
    public void ApplyStatus_StaleVersion_DoesNotNotifySubscribers()
    {
        var store = ConnectedStoreShowing(TestData.Status(version: 5));
        var notifications = 0;
        store.Changed += (_, _) => notifications++;

        store.ApplyStatus(TestData.Status(version: 4));

        notifications.ShouldBe(0);
    }

    [Fact]
    public void ApplyStatus_FirstStatusAfterReconnecting_IsAcceptedEvenWithALowerVersion()
    {
        var store = ConnectedStoreShowing(TestData.Status(floor: 7, version: 120));
        store.SetConnection(ConnectionState.Reconnecting);

        var applied = store.ApplyStatus(TestData.Status(floor: 1, version: 2));

        applied.ShouldBeTrue("a restarted server counts versions from the start again, and it is the only live source");
        store.Status!.CurrentFloor.ShouldBe(1);
    }

    [Fact]
    public void ApplyStatus_AfterResynchronising_IgnoresStaleVersionsAgain()
    {
        var store = ConnectedStoreShowing(TestData.Status(version: 120));
        store.SetConnection(ConnectionState.Reconnecting);
        store.ApplyStatus(TestData.Status(floor: 2, version: 2));
        store.SetConnection(ConnectionState.Connected);

        var applied = store.ApplyStatus(TestData.Status(floor: 8, version: 1));

        applied.ShouldBeFalse();
        store.Status!.CurrentFloor.ShouldBe(2);
    }

    [Fact]
    public void AddActivity_KeepsNewestFirst()
    {
        var store = new ElevatorStateStore();

        store.AddActivity(TestData.Moved(1, 2, secondsAfterNoon: 1));
        store.AddActivity(TestData.DoorsOpened(2, secondsAfterNoon: 2));

        store.Activity.Select(activity => activity.Kind).ShouldBe([ElevatorActivityKind.DoorsOpened, ElevatorActivityKind.Moved]);
    }

    [Fact]
    public void AddActivity_BeyondCapacity_DropsTheOldestEntries()
    {
        var store = new ElevatorStateStore(activityCapacity: 3);

        for (var second = 1; second <= 5; second++)
        {
            store.AddActivity(TestData.DoorsOpened(floor: second, secondsAfterNoon: second));
        }

        store.Activity.Count.ShouldBe(3);
        store.Activity.Select(activity => activity.Floor).ShouldBe([5, 4, 3]);
    }

    [Fact]
    public void AddActivity_RaisesActivityReceivedWithTheEntry()
    {
        var store = new ElevatorStateStore();
        ElevatorActivityDto? received = null;
        store.ActivityReceived += (_, activity) => received = activity;
        var opened = TestData.DoorsOpened(4);

        store.AddActivity(opened);

        received.ShouldBe(opened);
    }

    [Fact]
    public void Constructor_ZeroCapacity_IsRejected() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new ElevatorStateStore(activityCapacity: 0));

    [Fact]
    public void SetConnection_SameState_DoesNotNotify()
    {
        var store = new ElevatorStateStore();
        store.SetConnection(ConnectionState.Connected);
        var notifications = 0;
        store.Changed += (_, _) => notifications++;

        store.SetConnection(ConnectionState.Connected);

        notifications.ShouldBe(0);
    }

    [Fact]
    public void ApplyOccupancy_KeepsTheNewestCounts_AndIgnoresOlderOnes()
    {
        var store = new ElevatorStateStore();
        var newer = FakeStaffApiClient.OccupancyOf(inCar: 4) with { TakenAt = TestData.Noon.AddSeconds(5) };
        var older = FakeStaffApiClient.OccupancyOf(inCar: 1);

        store.ApplyOccupancy(newer).ShouldBeTrue();
        store.ApplyOccupancy(older).ShouldBeFalse();

        store.Occupancy.ShouldBe(newer);
    }

    [Fact]
    public void ClearOccupancy_ForgetsTheCounts_AndNotifies()
    {
        var store = new ElevatorStateStore();
        store.ApplyOccupancy(FakeStaffApiClient.OccupancyOf(inCar: 2));
        var notified = 0;
        store.Changed += (_, _) => notified++;

        store.ClearOccupancy();

        store.Occupancy.ShouldBeNull();
        notified.ShouldBe(1);
    }

    [Fact]
    public void Connection_BeforeAnyAttempt_IsConnecting() =>
        new ElevatorStateStore().Connection.ShouldBe(ConnectionState.Connecting);

    [Fact]
    public void ServerClockOffset_IsLearntFromTheStatuses_KeepingTheLargestSample()
    {
        var clock = new FakeTimeProvider(TestData.Noon);
        var store = new ElevatorStateStore(clock);
        store.ServerClockOffset.ShouldBeNull();

        store.ApplyStatus(TestData.Status(version: 1) with { TakenAt = TestData.Noon.AddSeconds(12) });
        store.ApplyStatus(TestData.Status(version: 1) with { TakenAt = TestData.Noon.AddMinutes(-10) });

        store.ServerClockOffset.ShouldBe(TimeSpan.FromSeconds(12), "an old cached copy never lowers the estimate");
        store.ServerNow.ShouldBe(TestData.Noon.AddSeconds(12));
    }

    [Fact]
    public void ServerClockOffset_IsForgotten_WhenTheConnectionDrops()
    {
        var clock = new FakeTimeProvider(TestData.Noon);
        var store = new ElevatorStateStore(clock);
        store.SetConnection(ConnectionState.Connected);
        store.ApplyStatus(TestData.Status(version: 1) with { TakenAt = TestData.Noon.AddSeconds(12) });

        store.SetConnection(ConnectionState.Reconnecting);

        store.ServerClockOffset.ShouldBeNull("the next server may be another instance with another clock");
        store.ServerNow.ShouldBe(TestData.Noon);
    }

    private static ElevatorStateStore ConnectedStoreShowing(ElevatorStatusDto status)
    {
        var store = new ElevatorStateStore();
        store.SetConnection(ConnectionState.Connected);
        store.ApplyStatus(status);
        return store;
    }
}
