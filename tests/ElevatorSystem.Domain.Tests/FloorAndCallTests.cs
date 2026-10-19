using static ElevatorSystem.Domain.Tests.TestElevators;

namespace ElevatorSystem.Domain.Tests;

public sealed class FloorAndCallTests
{
    [Fact]
    public void Floor_ComparesByNumber()
    {
        var three = new Floor(3);
        var seven = new Floor(7);

        (three < seven).ShouldBeTrue();
        (seven > three).ShouldBeTrue();
        (three <= new Floor(3)).ShouldBeTrue();
        (seven >= new Floor(7)).ShouldBeTrue();
        three.CompareTo(seven).ShouldBeLessThan(0);
    }

    [Fact]
    public void Floor_AboveAndBelow_AreAdjacentFloors()
    {
        new Floor(4).Above.ShouldBe(new Floor(5));
        new Floor(4).Below.ShouldBe(new Floor(3));
    }

    [Fact]
    public void Calls_AreEqualOnlyForTheSameKindFloorAndDirection()
    {
        Hall(5, Direction.Up).ShouldBe(Hall(5, Direction.Up));
        Hall(5, Direction.Up).ShouldNotBe(Hall(5, Direction.Down));
        ((ElevatorCall)Hall(5, Direction.Up)).ShouldNotBe(Car(5));
        Car(5).ShouldBe(Car(5));
    }

    [Fact]
    public void Calls_DescribeThemselvesForLogs()
    {
        Hall(7, Direction.Down).ToString().ShouldBe("hall call at floor 7 going down");
        Car(2).ToString().ShouldBe("car call to floor 2");
    }

    [Fact]
    public void Direction_Opposite_FlipsTheDirection()
    {
        Direction.Up.Opposite().ShouldBe(Direction.Down);
        Direction.Down.Opposite().ShouldBe(Direction.Up);
    }

    [Fact]
    public void RequestId_New_IsUniqueAndOrderedByCreation()
    {
        var clock = Clock();
        var first = RequestId.New(clock);
        clock.Advance(TimeSpan.FromMilliseconds(5));
        var second = RequestId.New(clock);

        first.ShouldNotBe(second);
        first.Value.CompareTo(second.Value).ShouldBeLessThan(0, "version 7 identifiers sort by creation time");
    }
}
