using ElevatorSystem.Domain;
using ElevatorSystem.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace ElevatorSystem.Persistence.Tests;

[Collection(PostgresTestGroup.Name)]
public sealed class PostgresElevatorStateStoreTests(PostgresFixture postgres)
{
    private readonly FakeTimeProvider _clock = new();

    public static TheoryData<ElevatorState, OutOfServiceReason?> EveryState => new()
    {
        { ElevatorState.Idle, null },
        { ElevatorState.MovingUp, null },
        { ElevatorState.MovingDown, null },
        { ElevatorState.DoorOpen, null },
        { ElevatorState.OutOfService, OutOfServiceReason.Maintenance },
        { ElevatorState.OutOfService, OutOfServiceReason.Fault },
        { ElevatorState.OutOfService, OutOfServiceReason.EmergencyStop },
    };

    [Fact]
    public async Task LoadAsync_NothingSaved_ReturnsNull()
    {
        await using var database = await postgres.CreateMigratedDatabaseAsync();
        var store = new PostgresElevatorStateStore(database.ContextFactory);

        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        loaded.ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(EveryState))]
    public async Task SaveAsync_ThenLoad_RoundTripsEveryStateThroughRestore(ElevatorState state, OutOfServiceReason? reason)
    {
        await using var database = await postgres.CreateMigratedDatabaseAsync();
        var store = new PostgresElevatorStateStore(database.ContextFactory);
        var saved = ElevatorIn(state, reason).Snapshot();

        await store.SaveAsync(saved, TestContext.Current.CancellationToken);
        var loaded = (await store.LoadAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();

        ShouldMatch(loaded, saved);
        ShouldMatch(
            Elevator.Restore(Building.Default, _clock, loaded).Snapshot(),
            Elevator.Restore(Building.Default, _clock, saved).Snapshot());
    }

    [Fact]
    public async Task SaveAsync_MergedCalls_KeepTheirCountsAndFifoOrder()
    {
        await using var database = await postgres.CreateMigratedDatabaseAsync();
        var store = new PostgresElevatorStateStore(database.ContextFactory);
        var elevator = new Elevator(Building.Default, _clock, new Floor(5));
        Press(elevator, new HallCall(new Floor(8), Direction.Down));
        Press(elevator, new CarCall(new Floor(1)));
        Press(elevator, new HallCall(new Floor(8), Direction.Down));
        Press(elevator, new HallCall(new Floor(8), Direction.Up));
        Press(elevator, new HallCall(new Floor(8), Direction.Down));
        Press(elevator, new CarCall(new Floor(1)));
        var saved = elevator.Snapshot();

        await store.SaveAsync(saved, TestContext.Current.CancellationToken);
        var loaded = (await store.LoadAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();

        loaded.PendingCalls.Select(pending => (pending.Call, pending.RequestCount)).ShouldBe(
        [
            (new HallCall(new Floor(8), Direction.Down), 3),
            (new CarCall(new Floor(1)), 2),
            (new HallCall(new Floor(8), Direction.Up), 1),
        ]);
        Elevator.Restore(Building.Default, _clock, loaded).Snapshot().PendingCalls.ShouldBe(saved.PendingCalls);
    }

    [Fact]
    public async Task SaveAsync_StaleVersion_DoesNotOverwriteTheNewerSnapshot()
    {
        await using var database = await postgres.CreateMigratedDatabaseAsync();
        var store = new PostgresElevatorStateStore(database.ContextFactory);
        var newer = Snapshot(version: 10, floor: 6, new CarCall(new Floor(9)));
        var stale = Snapshot(version: 9, floor: 2, new CarCall(new Floor(1)), new HallCall(new Floor(4), Direction.Up));

        await store.SaveAsync(newer, TestContext.Current.CancellationToken);
        await store.SaveAsync(stale, TestContext.Current.CancellationToken);

        ShouldMatch((await store.LoadAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull(), newer);
    }

    [Fact]
    public async Task SaveAsync_SameVersion_KeepsTheSnapshotAlreadySaved()
    {
        await using var database = await postgres.CreateMigratedDatabaseAsync();
        var store = new PostgresElevatorStateStore(database.ContextFactory);
        var first = Snapshot(version: 4, floor: 3, new CarCall(new Floor(7)));

        await store.SaveAsync(first, TestContext.Current.CancellationToken);
        await store.SaveAsync(Snapshot(version: 4, floor: 8), TestContext.Current.CancellationToken);

        ShouldMatch((await store.LoadAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull(), first);
    }

    [Fact]
    public async Task SaveAsync_NewerVersion_ReplacesStateAndEveryPendingCall()
    {
        await using var database = await postgres.CreateMigratedDatabaseAsync();
        var store = new PostgresElevatorStateStore(database.ContextFactory);
        var newer = Snapshot(version: 2, floor: 4, new HallCall(new Floor(2), Direction.Up));

        await store.SaveAsync(Snapshot(version: 1, floor: 3, new CarCall(new Floor(7)), new CarCall(new Floor(9))), TestContext.Current.CancellationToken);
        await store.SaveAsync(newer, TestContext.Current.CancellationToken);

        ShouldMatch((await store.LoadAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull(), newer);
    }

    [Fact]
    public async Task SaveAsync_ConcurrentSaves_KeepExactlyTheHighestVersion()
    {
        await using var database = await postgres.CreateMigratedDatabaseAsync();
        var store = new PostgresElevatorStateStore(database.ContextFactory);
        var snapshots = Enumerable.Range(1, 120)
            .Select(version => Snapshot(
                version,
                floor: 1 + (version % 10),
                new CarCall(new Floor(1 + ((version + 3) % 10))),
                new HallCall(new Floor(2 + (version % 8)), version % 2 == 0 ? Direction.Up : Direction.Down)))
            .ToArray();
        Random.Shared.Shuffle(snapshots);

        await Parallel.ForEachAsync(
            snapshots,
            new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = TestContext.Current.CancellationToken },
            async (snapshot, cancellationToken) => await store.SaveAsync(snapshot, cancellationToken));

        var expected = snapshots.Single(snapshot => snapshot.Version == 120);
        ShouldMatch((await store.LoadAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull(), expected);
    }

    private Elevator ElevatorIn(ElevatorState state, OutOfServiceReason? reason)
    {
        var elevator = new Elevator(Building.Default, _clock, new Floor(5));
        Press(elevator, new HallCall(new Floor(9), Direction.Down));
        Press(elevator, new CarCall(new Floor(2)));
        Press(elevator, new HallCall(new Floor(3), Direction.Up));

        var transition = state switch
        {
            ElevatorState.Idle => null,
            ElevatorState.MovingUp => elevator.MoveUp(),
            ElevatorState.MovingDown => elevator.MoveDown(),
            ElevatorState.DoorOpen => elevator.OpenDoor(),
            ElevatorState.OutOfService => elevator.TakeOutOfService(reason.ShouldNotBeNull()),
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
        transition?.Succeeded.ShouldBeTrue();
        elevator.State.ShouldBe(state);
        return elevator;
    }

    private void Press(Elevator elevator, ElevatorCall call)
    {
        _clock.Advance(TimeSpan.FromMilliseconds(250));
        elevator.AddRequest(new PassengerRequest(RequestId.New(_clock), call, _clock.GetUtcNow()));
    }

    private ElevatorSnapshot Snapshot(long version, int floor, params ElevatorCall[] calls)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        return new ElevatorSnapshot(
            new Floor(floor),
            ElevatorState.Idle,
            Direction.Up,
            null,
            [.. calls.Select(call => new PendingCall(call, RequestId.New(_clock), _clock.GetUtcNow(), 1))],
            version,
            _clock.GetUtcNow());
    }

    private static void ShouldMatch(ElevatorSnapshot actual, ElevatorSnapshot expected)
    {
        actual.PendingCalls.ShouldBe(expected.PendingCalls);
        (actual with { PendingCalls = expected.PendingCalls }).ShouldBe(expected);
    }
}
