using static ElevatorSystem.Domain.Tests.TestElevators;

namespace ElevatorSystem.Domain.Tests;

/// <summary>Restoring an elevator from a saved snapshot after a restart (ADR-0007).</summary>
public sealed class ElevatorRestoreTests
{
    [Fact]
    public void Restore_IdleSnapshot_KeepsFloorDirectionVersionAndCallsInOrder()
    {
        var original = At(2);
        original.AddRequest(Request(Hall(7, Direction.Down)));
        original.AddRequest(Request(Hall(7, Direction.Down)));
        original.AddRequest(Request(Car(3)));
        original.MoveUp();
        original.OpenDoor();
        original.CloseDoor();
        var saved = original.Snapshot();

        var restored = Elevator.Restore(Building.Default, Clock(), saved).Snapshot();

        restored.CurrentFloor.ShouldBe(saved.CurrentFloor);
        restored.State.ShouldBe(ElevatorState.Idle);
        restored.TravelDirection.ShouldBe(Direction.Up);
        restored.Version.ShouldBe(saved.Version);
        restored.PendingCalls.ShouldBe(saved.PendingCalls);
    }

    [Theory]
    [InlineData(ElevatorState.MovingUp)]
    [InlineData(ElevatorState.MovingDown)]
    [InlineData(ElevatorState.DoorOpen)]
    public void Restore_TransientState_ComesBackIdleAtTheRecordedFloor(ElevatorState state)
    {
        var saved = InState(state, floor: 6).Snapshot();

        var restored = Elevator.Restore(Building.Default, Clock(), saved);

        restored.State.ShouldBe(ElevatorState.Idle, "after a restart the car has stopped");
        restored.CurrentFloor.ShouldBe(new Floor(6));
        restored.MoveUp().Succeeded.ShouldBeTrue("the restored car can move again");
    }

    [Theory]
    [InlineData(OutOfServiceReason.Fault)]
    [InlineData(OutOfServiceReason.EmergencyStop)]
    [InlineData(OutOfServiceReason.Maintenance)]
    public void Restore_OutOfService_StaysOutOfServiceForTheSameReason(OutOfServiceReason reason)
    {
        var original = At(4);
        original.TakeOutOfService(reason);

        var restored = Elevator.Restore(Building.Default, Clock(), original.Snapshot());

        restored.Snapshot().OutOfServiceReason.ShouldBe(reason);
        restored.MoveUp().Error.ShouldBe(TransitionError.OutOfService, "a fault survives a restart");
    }

    [Fact]
    public void Restore_DropsInvalidDuplicateAndEmptyCalls()
    {
        var calls = SnapshotAt(5, null, Car(3), Car(11), Hall(10, Direction.Up), Car(3), Hall(2, Direction.Down));
        var withEmpty = calls with
        {
            PendingCalls = [.. calls.PendingCalls, new PendingCall(Car(8), RequestId.New(TimeProvider.System), Start, 0)],
        };

        var restored = Elevator.Restore(Building.Default, Clock(), withEmpty).Snapshot();

        restored.PendingCalls.Select(call => call.Call).ShouldBe([Car(3), Hall(2, Direction.Down)]);
    }

    [Fact]
    public void Restore_FloorOutsideTheBuilding_Throws()
    {
        var saved = SnapshotAt(5, null) with { CurrentFloor = new Floor(12) };

        Should.Throw<ArgumentOutOfRangeException>(() => Elevator.Restore(Building.Default, Clock(), saved));
    }

    [Fact]
    public void Restore_ThenServe_ServesTheRestoredCalls()
    {
        var original = At(1);
        original.AddRequest(Request(Car(2)));
        var restored = Elevator.Restore(Building.Default, Clock(), original.Snapshot());

        restored.MoveUp();
        var opened = restored.OpenDoor();

        opened.Events.OfType<Events.CallServed>().ShouldHaveSingleItem().Call.Call.ShouldBe(Car(2));
    }
}
