using static ElevatorSystem.Domain.Tests.TestElevators;

namespace ElevatorSystem.Domain.Tests;

public sealed class BuildingTests
{
    [Fact]
    public void Default_ServesFloorsOneToTen()
    {
        var building = Building.Default;

        building.LowestFloor.ShouldBe(new Floor(1));
        building.HighestFloor.ShouldBe(new Floor(10));
        building.FloorCount.ShouldBe(10);
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(10, 1)]
    public void Constructor_LowestNotBelowHighest_Throws(int lowest, int highest)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new Building(lowest, highest));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(10, true)]
    [InlineData(0, false)]
    [InlineData(11, false)]
    [InlineData(-3, false)]
    public void Contains_ReflectsTheFloorRange(int floor, bool expected)
    {
        Building.Default.Contains(new Floor(floor)).ShouldBe(expected);
    }

    [Fact]
    public void MaxPendingCalls_CountsEveryDistinctCallThatCanBeMade()
    {
        Building.Default.MaxPendingCalls.ShouldBe(28);
        Building.Default.MaxPendingCalls.ShouldBe(AllValidCalls(Building.Default).Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void Validate_FloorOutsideBuilding_IsInvalidFloor(int floor)
    {
        Building.Default.Validate(Car(floor)).ShouldBe(RequestRejectionReason.InvalidFloor);
        Building.Default.Validate(Hall(floor, Direction.Up)).ShouldBe(RequestRejectionReason.InvalidFloor);
    }

    [Theory]
    [InlineData(10, Direction.Up)]
    [InlineData(1, Direction.Down)]
    [InlineData(5, (Direction)42)]
    public void Validate_ImpossibleHallCallDirection_IsInvalidDirection(int floor, Direction direction)
    {
        Building.Default.Validate(Hall(floor, direction)).ShouldBe(RequestRejectionReason.InvalidDirection);
    }

    [Theory]
    [InlineData(1, Direction.Up)]
    [InlineData(10, Direction.Down)]
    [InlineData(5, Direction.Up)]
    [InlineData(5, Direction.Down)]
    public void Validate_PossibleHallCall_IsValid(int floor, Direction direction)
    {
        Building.Default.Validate(Hall(floor, direction)).ShouldBeNull();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public void Validate_CarCallToAnyFloorInside_IsValid(int floor)
    {
        Building.Default.Validate(Car(floor)).ShouldBeNull();
    }
}
