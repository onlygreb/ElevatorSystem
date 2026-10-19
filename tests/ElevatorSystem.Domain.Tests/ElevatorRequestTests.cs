using ElevatorSystem.Domain.Events;
using static ElevatorSystem.Domain.Tests.TestElevators;

namespace ElevatorSystem.Domain.Tests;

public sealed class ElevatorRequestTests
{
    [Fact]
    public void AddRequest_NewCalls_AreQueuedInArrivalOrder()
    {
        var elevator = At(1);

        var first = elevator.AddRequest(Request(Hall(7, Direction.Down)));
        var second = elevator.AddRequest(Request(Car(3)));

        first.Outcome.ShouldBe(AddRequestOutcome.Queued);
        first.Events.ShouldHaveSingleItem().ShouldBeOfType<CallQueued>().Position.ShouldBe(1);
        second.Events.ShouldHaveSingleItem().ShouldBeOfType<CallQueued>().Position.ShouldBe(2);
        elevator.TargetFloors.ShouldBe([new Floor(7), new Floor(3)]);
    }

    [Fact]
    public void AddRequest_RepeatedCall_IsMergedAndKeepsItsPlaceInLine()
    {
        var elevator = At(1);
        elevator.AddRequest(Request(Hall(5, Direction.Up)));
        elevator.AddRequest(Request(Car(3)));

        var repeat = elevator.AddRequest(Request(Hall(5, Direction.Up)));

        repeat.Outcome.ShouldBe(AddRequestOutcome.Merged);
        repeat.Events.ShouldHaveSingleItem().ShouldBeOfType<CallMerged>().Position.ShouldBe(1);
        var pending = elevator.Snapshot().PendingCalls;
        pending.Length.ShouldBe(2);
        pending[0].Call.ShouldBe(Hall(5, Direction.Up));
        pending[0].RequestCount.ShouldBe(2);
    }

    [Fact]
    public void AddRequest_DifferentCallsForTheSameFloor_AreKeptApartButTargetTheFloorOnce()
    {
        var elevator = At(1);

        elevator.AddRequest(Request(Hall(5, Direction.Up)));
        elevator.AddRequest(Request(Hall(5, Direction.Down)));
        elevator.AddRequest(Request(Car(5)));

        elevator.Snapshot().PendingCalls.Length.ShouldBe(3);
        elevator.TargetFloors.ShouldBe([new Floor(5)]);
    }

    [Fact]
    public void TargetFloors_ListsEachFloorOnceInTheOrderFirstRequested()
    {
        var elevator = At(1);

        elevator.AddRequest(Request(Hall(7, Direction.Down)));
        elevator.AddRequest(Request(Car(3)));
        elevator.AddRequest(Request(Car(7)));
        elevator.AddRequest(Request(Hall(2, Direction.Up)));

        elevator.TargetFloors.ShouldBe([new Floor(7), new Floor(3), new Floor(2)]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(-3)]
    public void AddRequest_FloorOutsideBuilding_IsRejectedWithoutChangingAnything(int floor)
    {
        var elevator = At(1);
        var request = Request(Car(floor));

        var result = elevator.AddRequest(request);

        result.Outcome.ShouldBe(AddRequestOutcome.Rejected);
        result.RejectionReason.ShouldBe(RequestRejectionReason.InvalidFloor);
        result.Events.ShouldHaveSingleItem().ShouldBeOfType<RequestRejected>().Request.ShouldBe(request);
        elevator.Snapshot().PendingCalls.ShouldBeEmpty();
        elevator.Snapshot().Version.ShouldBe(0);
    }

    [Theory]
    [InlineData(10, Direction.Up)]
    [InlineData(1, Direction.Down)]
    public void AddRequest_ImpossibleDirection_IsRejected(int floor, Direction direction)
    {
        var result = At(5).AddRequest(Request(Hall(floor, direction)));

        result.RejectionReason.ShouldBe(RequestRejectionReason.InvalidDirection);
    }

    [Fact]
    public void AddRequest_WhileOutOfService_IsRejected()
    {
        var elevator = InState(ElevatorState.OutOfService);

        var result = elevator.AddRequest(Request(Car(3)));

        result.RejectionReason.ShouldBe(RequestRejectionReason.OutOfService);
        elevator.Snapshot().PendingCalls.ShouldBeEmpty();
    }

    [Fact]
    public void AddRequest_ForTheFloorWhereTheDoorIsOpen_IsServedAtOnce()
    {
        var elevator = InState(ElevatorState.DoorOpen, floor: 5);

        var result = elevator.AddRequest(Request(Hall(5, Direction.Up)));

        result.Outcome.ShouldBe(AddRequestOutcome.ServedImmediately);
        var served = result.Events.ShouldHaveSingleItem().ShouldBeOfType<CallServed>();
        served.WaitTime.ShouldBe(TimeSpan.Zero);
        elevator.Snapshot().PendingCalls.ShouldBeEmpty();
    }

    [Fact]
    public void AddRequest_ForAnotherFloorWhileTheDoorIsOpen_IsQueued()
    {
        var elevator = InState(ElevatorState.DoorOpen, floor: 5);

        elevator.AddRequest(Request(Car(8))).Outcome.ShouldBe(AddRequestOutcome.Queued);
    }

    [Fact]
    public void AddRequest_EveryPossibleCallManyTimes_NeverExceedsTheBuildingMaximum()
    {
        var elevator = At(1);
        var calls = AllValidCalls(Building.Default);

        for (var round = 0; round < 5; round++)
        {
            foreach (var call in calls)
            {
                elevator.AddRequest(Request(call));
            }
        }

        var pending = elevator.Snapshot().PendingCalls;
        pending.Length.ShouldBe(Building.Default.MaxPendingCalls);
        pending.Sum(call => call.RequestCount).ShouldBe(calls.Count * 5);
    }

    [Fact]
    public void AddRequest_Null_Throws()
    {
        Should.Throw<ArgumentNullException>(() => At(1).AddRequest(null!));
    }

    [Fact]
    public void OpenDoor_ServesEveryPendingCallForThatFloorAndReportsHowLongEachWaited()
    {
        var clock = Clock();
        var elevator = At(3, clock);
        elevator.AddRequest(Request(Hall(4, Direction.Up), clock));
        clock.Advance(TimeSpan.FromSeconds(2));
        elevator.AddRequest(Request(Car(4), clock));
        elevator.AddRequest(Request(Car(6), clock));
        elevator.MoveUp();
        clock.Advance(TimeSpan.FromSeconds(8));

        var result = elevator.OpenDoor();

        result.Events[0].ShouldBeOfType<DoorsOpened>().Floor.ShouldBe(new Floor(4));
        var served = result.Events.OfType<CallServed>().ToList();
        served.Select(e => e.Call.Call).ShouldBe([Hall(4, Direction.Up), Car(4)]);
        served.Select(e => e.WaitTime).ShouldBe([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(8)]);
        elevator.TargetFloors.ShouldBe([new Floor(6)]);
    }

    [Fact]
    public void Snapshot_IsUnaffectedByLaterChanges()
    {
        var elevator = At(1);
        var before = elevator.Snapshot();

        elevator.AddRequest(Request(Car(4)));
        elevator.MoveUp();

        before.PendingCalls.ShouldBeEmpty();
        before.CurrentFloor.ShouldBe(new Floor(1));
        elevator.Snapshot().Version.ShouldBeGreaterThan(before.Version);
    }
}
