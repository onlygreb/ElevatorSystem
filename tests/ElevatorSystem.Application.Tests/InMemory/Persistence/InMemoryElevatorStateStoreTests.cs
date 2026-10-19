using ElevatorSystem.Application.InMemory.Persistence;
using ElevatorSystem.Domain;
using Microsoft.Extensions.Time.Testing;

namespace ElevatorSystem.Application.Tests.InMemory.Persistence;

public sealed class InMemoryElevatorStateStoreTests
{
    private static readonly FakeTimeProvider Clock = new();

    [Fact]
    public async Task LoadAsync_NothingSaved_ReturnsNull()
    {
        var store = new InMemoryElevatorStateStore();

        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        loaded.ShouldBeNull();
    }

    [Fact]
    public async Task SaveAsync_ThenLoad_ReturnsASnapshotThatRestoresTheCar()
    {
        var store = new InMemoryElevatorStateStore();
        var elevator = new Elevator(Building.Default, Clock, new Floor(3));
        elevator.AddRequest(Request(new HallCall(new Floor(7), Direction.Down)));
        elevator.AddRequest(Request(new CarCall(new Floor(2))));
        elevator.MoveUp();

        await store.SaveAsync(elevator.Snapshot(), TestContext.Current.CancellationToken);
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        var restored = Elevator.Restore(Building.Default, Clock, loaded.ShouldNotBeNull()).Snapshot();
        restored.CurrentFloor.ShouldBe(new Floor(4));
        restored.PendingCalls.Select(pending => pending.Call).ShouldBe([new HallCall(new Floor(7), Direction.Down), new CarCall(new Floor(2))]);
    }

    [Fact]
    public async Task SaveAsync_LowerOrEqualVersion_KeepsTheNewerSnapshot()
    {
        var store = new InMemoryElevatorStateStore();
        var newer = Snapshot(floor: 5, version: 10);

        await store.SaveAsync(newer, TestContext.Current.CancellationToken);
        await store.SaveAsync(Snapshot(floor: 2, version: 9), TestContext.Current.CancellationToken);
        await store.SaveAsync(Snapshot(floor: 3, version: 10), TestContext.Current.CancellationToken);

        (await store.LoadAsync(TestContext.Current.CancellationToken)).ShouldBeSameAs(newer);
    }

    [Fact]
    public async Task SaveAsync_ConcurrentSaves_KeepsTheHighestVersion()
    {
        var store = new InMemoryElevatorStateStore();
        var versions = Enumerable.Range(1, 500).Reverse().ToArray();

        await Parallel.ForEachAsync(
            versions,
            TestContext.Current.CancellationToken,
            async (version, cancellationToken) => await store.SaveAsync(Snapshot(floor: 1 + (version % 10), version), cancellationToken));

        (await store.LoadAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull().Version.ShouldBe(500);
    }

    private static PassengerRequest Request(ElevatorCall call) => new(RequestId.New(Clock), call, Clock.GetUtcNow());

    private static ElevatorSnapshot Snapshot(int floor, long version) =>
        new(new Floor(floor), ElevatorState.Idle, null, null, [], version, Clock.GetUtcNow());
}
