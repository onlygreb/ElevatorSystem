using ElevatorSystem.Domain.Events;
using static ElevatorSystem.Domain.Tests.TestElevators;

namespace ElevatorSystem.Domain.Tests;

/// <summary>Clearing the queue (ADR-0032): every call but the kept ones is cancelled, and the car itself is untouched.</summary>
public sealed class ElevatorClearRequestsTests
{
    [Fact]
    public void ClearRequests_CancelsEveryCallButTheKeptOnes_WhichKeepTheirOrder()
    {
        var elevator = At(1);
        elevator.AddRequest(Request(Car(4)));
        elevator.AddRequest(Request(Hall(8, Direction.Down)));
        elevator.AddRequest(Request(Car(6)));
        elevator.AddRequest(Request(Hall(3, Direction.Up)));
        var before = elevator.Snapshot();

        var result = elevator.ClearRequests([Hall(3, Direction.Up), Car(4)]);

        var cleared = result.Events.ShouldHaveSingleItem().ShouldBeOfType<RequestsCleared>();
        cleared.CancelledCalls.Select(call => call.Call).ShouldBe([Hall(8, Direction.Down), Car(6)]);
        cleared.Floor.ShouldBe(new Floor(1));
        var after = elevator.Snapshot();
        after.PendingCalls.Select(call => call.Call).ShouldBe([Car(4), Hall(3, Direction.Up)], "the kept calls keep their place in line");
        after.Version.ShouldBeGreaterThan(before.Version);
    }

    [Fact]
    public void ClearRequests_WhileMoving_LeavesTheCarAsItIs()
    {
        var elevator = At(2);
        elevator.AddRequest(Request(Car(7)));
        elevator.MoveUp();

        elevator.ClearRequests([]);

        var snapshot = elevator.Snapshot();
        snapshot.State.ShouldBe(ElevatorState.MovingUp);
        snapshot.CurrentFloor.ShouldBe(new Floor(3));
        snapshot.PendingCalls.ShouldBeEmpty();
    }

    [Fact]
    public void ClearRequests_WithNothingToCancel_ChangesNothing()
    {
        var elevator = At(1);
        elevator.AddRequest(Request(Car(4)));
        var before = elevator.Snapshot();

        var result = elevator.ClearRequests([Car(4)]);

        result.Events.ShouldBeEmpty();
        elevator.Snapshot().Version.ShouldBe(before.Version);
    }
}
