using ElevatorSystem.Domain.Scheduling;
using static ElevatorSystem.Domain.Tests.TestElevators;

namespace ElevatorSystem.Domain.Tests.Scheduling;

public sealed class FifoSchedulingStrategyTests
{
    private readonly FifoSchedulingStrategy _fifo = new();

    [Fact]
    public void SelectTarget_NothingPending_ReturnsNull()
    {
        _fifo.SelectTarget(SnapshotAt(5, null)).ShouldBeNull();
    }

    [Fact]
    public void SelectTarget_ReturnsTheOldestCall_EvenWhenOthersAreCloser()
    {
        var snapshot = SnapshotAt(1, null, Car(8), Hall(2, Direction.Up));

        _fifo.SelectTarget(snapshot).ShouldBe(new Floor(8));
    }

    [Fact]
    public void SelectTarget_OldestCallAtTheCurrentFloor_ReturnsTheCurrentFloor()
    {
        _fifo.SelectTarget(SnapshotAt(4, Direction.Up, Hall(4, Direction.Down), Car(9))).ShouldBe(new Floor(4));
    }
}

public sealed class LookSchedulingStrategyTests
{
    private readonly LookSchedulingStrategy _look = new();

    [Fact]
    public void SelectTarget_NothingPending_ReturnsNull()
    {
        _look.SelectTarget(SnapshotAt(5, Direction.Up)).ShouldBeNull();
    }

    [Fact]
    public void SelectTarget_CallAtTheCurrentFloor_StopsHereFirst()
    {
        _look.SelectTarget(SnapshotAt(5, Direction.Up, Car(9), Car(5))).ShouldBe(new Floor(5));
    }

    [Fact]
    public void SelectTarget_GoingUp_PicksTheNearestFloorAbove()
    {
        _look.SelectTarget(SnapshotAt(4, Direction.Up, Car(2), Car(9), Car(6))).ShouldBe(new Floor(6));
    }

    [Fact]
    public void SelectTarget_GoingUpWithNothingAbove_ReversesToTheNearestFloorBelow()
    {
        _look.SelectTarget(SnapshotAt(8, Direction.Up, Car(2), Car(5))).ShouldBe(new Floor(5));
    }

    [Fact]
    public void SelectTarget_GoingDown_PicksTheNearestFloorBelow()
    {
        _look.SelectTarget(SnapshotAt(6, Direction.Down, Car(9), Car(3), Car(5))).ShouldBe(new Floor(5));
    }

    [Theory]
    [InlineData(new[] { 2, 6 }, 2)]
    [InlineData(new[] { 9, 4, 7 }, 7)]
    public void SelectTarget_BeforeTheFirstTrip_HeadsTowardsTheOldestCall(int[] floors, int expected)
    {
        var snapshot = SnapshotAt(5, null, [.. floors.Select(floor => (ElevatorCall)Car(floor))]);

        _look.SelectTarget(snapshot).ShouldBe(new Floor(expected));
    }

    [Fact]
    public void Name_IsWhatTheConfigurationAndStatusShow()
    {
        new FifoSchedulingStrategy().Name.ShouldBe("FIFO");
        new LookSchedulingStrategy().Name.ShouldBe("LOOK");
    }
}
