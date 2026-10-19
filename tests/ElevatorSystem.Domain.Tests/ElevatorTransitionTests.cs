using ElevatorSystem.Domain.Events;
using static ElevatorSystem.Domain.Tests.TestElevators;

namespace ElevatorSystem.Domain.Tests;

public enum ElevatorCommand
{
    MoveUp,
    MoveDown,
    OpenDoor,
    CloseDoor,
}

public sealed class ElevatorTransitionTests
{
    /// <summary>Every (state, command) pair: the state it leads to, or why it is refused.</summary>
    public static TheoryData<ElevatorState, ElevatorCommand, ElevatorState, TransitionError?> StateTable => new()
    {
        { ElevatorState.Idle, ElevatorCommand.MoveUp, ElevatorState.MovingUp, null },
        { ElevatorState.Idle, ElevatorCommand.MoveDown, ElevatorState.MovingDown, null },
        { ElevatorState.Idle, ElevatorCommand.OpenDoor, ElevatorState.DoorOpen, null },
        { ElevatorState.Idle, ElevatorCommand.CloseDoor, ElevatorState.Idle, null },
        { ElevatorState.MovingUp, ElevatorCommand.MoveUp, ElevatorState.MovingUp, null },
        { ElevatorState.MovingUp, ElevatorCommand.MoveDown, ElevatorState.MovingUp, TransitionError.MovingInOppositeDirection },
        { ElevatorState.MovingUp, ElevatorCommand.OpenDoor, ElevatorState.DoorOpen, null },
        { ElevatorState.MovingUp, ElevatorCommand.CloseDoor, ElevatorState.MovingUp, null },
        { ElevatorState.MovingDown, ElevatorCommand.MoveUp, ElevatorState.MovingDown, TransitionError.MovingInOppositeDirection },
        { ElevatorState.MovingDown, ElevatorCommand.MoveDown, ElevatorState.MovingDown, null },
        { ElevatorState.MovingDown, ElevatorCommand.OpenDoor, ElevatorState.DoorOpen, null },
        { ElevatorState.MovingDown, ElevatorCommand.CloseDoor, ElevatorState.MovingDown, null },
        { ElevatorState.DoorOpen, ElevatorCommand.MoveUp, ElevatorState.DoorOpen, TransitionError.DoorIsOpen },
        { ElevatorState.DoorOpen, ElevatorCommand.MoveDown, ElevatorState.DoorOpen, TransitionError.DoorIsOpen },
        { ElevatorState.DoorOpen, ElevatorCommand.OpenDoor, ElevatorState.DoorOpen, null },
        { ElevatorState.DoorOpen, ElevatorCommand.CloseDoor, ElevatorState.Idle, null },
        { ElevatorState.OutOfService, ElevatorCommand.MoveUp, ElevatorState.OutOfService, TransitionError.OutOfService },
        { ElevatorState.OutOfService, ElevatorCommand.MoveDown, ElevatorState.OutOfService, TransitionError.OutOfService },
        { ElevatorState.OutOfService, ElevatorCommand.OpenDoor, ElevatorState.OutOfService, TransitionError.OutOfService },
        { ElevatorState.OutOfService, ElevatorCommand.CloseDoor, ElevatorState.OutOfService, TransitionError.OutOfService },
    };

    [Theory]
    [MemberData(nameof(StateTable))]
    public void Transition_FromEveryState_FollowsTheStateTable(
        ElevatorState from, ElevatorCommand command, ElevatorState expectedState, TransitionError? expectedError)
    {
        var elevator = InState(from);
        var before = elevator.Snapshot();

        var result = Apply(elevator, command);

        var after = elevator.Snapshot();
        result.Error.ShouldBe(expectedError);
        after.State.ShouldBe(expectedState);
        if (!result.Succeeded)
        {
            after.Version.ShouldBe(before.Version, "a refused transition must not change anything");
            after.CurrentFloor.ShouldBe(before.CurrentFloor);
            result.Events.ShouldBeEmpty();
        }
    }

    [Fact]
    public void Constructor_WithoutStartFloor_StartsIdleAtTheLowestFloor()
    {
        var elevator = new Elevator(Building.Default, Clock());

        elevator.CurrentFloor.ShouldBe(new Floor(1));
        elevator.State.ShouldBe(ElevatorState.Idle);
        elevator.TargetFloors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Constructor_StartFloorOutsideBuilding_Throws(int floor)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new Elevator(Building.Default, Clock(), new Floor(floor)));
    }

    [Fact]
    public void MoveUp_FromIdle_AdvancesExactlyOneFloorAndReportsIt()
    {
        var elevator = At(4);

        var result = elevator.MoveUp();

        elevator.CurrentFloor.ShouldBe(new Floor(5));
        elevator.State.ShouldBe(ElevatorState.MovingUp);
        var moved = result.Events.ShouldHaveSingleItem().ShouldBeOfType<ElevatorMoved>();
        moved.From.ShouldBe(new Floor(4));
        moved.Floor.ShouldBe(new Floor(5));
        moved.Direction.ShouldBe(Direction.Up);
    }

    [Fact]
    public void MoveDown_FromIdle_DescendsExactlyOneFloorAndReportsIt()
    {
        var elevator = At(4);

        var result = elevator.MoveDown();

        elevator.CurrentFloor.ShouldBe(new Floor(3));
        elevator.State.ShouldBe(ElevatorState.MovingDown);
        result.Events.ShouldHaveSingleItem().ShouldBeOfType<ElevatorMoved>().Direction.ShouldBe(Direction.Down);
    }

    [Fact]
    public void MoveUp_AtTopFloor_IsRejected()
    {
        var elevator = At(10);

        var result = elevator.MoveUp();

        result.Error.ShouldBe(TransitionError.AtTopFloor);
        elevator.CurrentFloor.ShouldBe(new Floor(10));
        elevator.State.ShouldBe(ElevatorState.Idle);
    }

    [Fact]
    public void MoveDown_AtBottomFloor_IsRejected()
    {
        var elevator = At(1);

        var result = elevator.MoveDown();

        result.Error.ShouldBe(TransitionError.AtBottomFloor);
        elevator.CurrentFloor.ShouldBe(new Floor(1));
    }

    [Fact]
    public void MoveUp_Repeatedly_StopsAtTheTopFloor()
    {
        var elevator = At(1);

        var results = Enumerable.Range(0, 12).Select(_ => elevator.MoveUp()).ToList();

        results.Count(result => result.Succeeded).ShouldBe(9);
        results.Last().Error.ShouldBe(TransitionError.AtTopFloor);
        elevator.CurrentFloor.ShouldBe(new Floor(10));
    }

    [Fact]
    public void Snapshot_TracksTheDirectionOfTheLatestTrip()
    {
        var elevator = At(5);
        elevator.Snapshot().TravelDirection.ShouldBeNull();

        elevator.MoveUp();
        elevator.OpenDoor();
        elevator.CloseDoor();

        elevator.Snapshot().TravelDirection.ShouldBe(Direction.Up, "an idle car remembers where it was heading");
        elevator.MoveDown();
        elevator.Snapshot().TravelDirection.ShouldBe(Direction.Down);
    }

    [Fact]
    public void OpenDoor_WhileMoving_StopsAtTheFloorBeingReached()
    {
        var elevator = InState(ElevatorState.MovingUp, floor: 6);

        var result = elevator.OpenDoor();

        result.Events.OfType<DoorsOpened>().ShouldHaveSingleItem().Floor.ShouldBe(new Floor(6));
        elevator.CurrentFloor.ShouldBe(new Floor(6));
    }

    [Fact]
    public void CloseDoor_WhenOpen_LeavesTheCarIdleAndReportsIt()
    {
        var elevator = InState(ElevatorState.DoorOpen, floor: 3);

        var result = elevator.CloseDoor();

        elevator.State.ShouldBe(ElevatorState.Idle);
        result.Events.ShouldHaveSingleItem().ShouldBeOfType<DoorsClosed>().Floor.ShouldBe(new Floor(3));
    }

    [Theory]
    [InlineData(ElevatorState.Idle)]
    [InlineData(ElevatorState.DoorOpen)]
    public void UnchangedTransitions_ProduceNoEventsAndKeepTheVersion(ElevatorState state)
    {
        var elevator = InState(state);
        var before = elevator.Snapshot().Version;

        var result = state == ElevatorState.Idle ? elevator.CloseDoor() : elevator.OpenDoor();

        result.Succeeded.ShouldBeTrue();
        result.Events.ShouldBeEmpty();
        elevator.Snapshot().Version.ShouldBe(before);
    }

    private static TransitionResult Apply(Elevator elevator, ElevatorCommand command) => command switch
    {
        ElevatorCommand.MoveUp => elevator.MoveUp(),
        ElevatorCommand.MoveDown => elevator.MoveDown(),
        ElevatorCommand.OpenDoor => elevator.OpenDoor(),
        ElevatorCommand.CloseDoor => elevator.CloseDoor(),
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
    };
}
