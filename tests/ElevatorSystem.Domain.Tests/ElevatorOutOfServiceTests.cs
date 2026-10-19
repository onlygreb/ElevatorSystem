using ElevatorSystem.Domain.Events;
using static ElevatorSystem.Domain.Tests.TestElevators;

namespace ElevatorSystem.Domain.Tests;

public sealed class ElevatorOutOfServiceTests
{
    public static TheoryData<ElevatorState, OutOfServiceReason> EveryStateAndReason
    {
        get
        {
            var data = new TheoryData<ElevatorState, OutOfServiceReason>();
            foreach (var state in Enum.GetValues<ElevatorState>().Where(state => state != ElevatorState.OutOfService))
            {
                foreach (var reason in Enum.GetValues<OutOfServiceReason>())
                {
                    data.Add(state, reason);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(EveryStateAndReason))]
    public void TakeOutOfService_FromAnyState_TakesEffectImmediately(ElevatorState state, OutOfServiceReason reason)
    {
        var elevator = InState(state);

        var result = elevator.TakeOutOfService(reason);

        result.Succeeded.ShouldBeTrue();
        var snapshot = elevator.Snapshot();
        snapshot.State.ShouldBe(ElevatorState.OutOfService);
        snapshot.OutOfServiceReason.ShouldBe(reason);
        result.Events.ShouldHaveSingleItem().ShouldBeOfType<ElevatorTakenOutOfService>().Reason.ShouldBe(reason);
    }

    [Fact]
    public void TakeOutOfService_CancelsEveryPendingCallAndReportsThem()
    {
        var elevator = At(1);
        elevator.AddRequest(Request(Car(4)));
        elevator.AddRequest(Request(Hall(8, Direction.Down)));

        var result = elevator.TakeOutOfService(OutOfServiceReason.EmergencyStop);

        var taken = result.Events.ShouldHaveSingleItem().ShouldBeOfType<ElevatorTakenOutOfService>();
        taken.CancelledCalls.Select(call => call.Call).ShouldBe([Car(4), Hall(8, Direction.Down)]);
        elevator.Snapshot().PendingCalls.ShouldBeEmpty();
    }

    [Fact]
    public void TakeOutOfService_AgainForTheSameReason_ChangesNothing()
    {
        var elevator = InState(ElevatorState.OutOfService);
        var version = elevator.Snapshot().Version;

        var result = elevator.TakeOutOfService(OutOfServiceReason.Maintenance);

        result.Events.ShouldBeEmpty();
        elevator.Snapshot().Version.ShouldBe(version);
    }

    [Fact]
    public void TakeOutOfService_ForADifferentReason_UpdatesTheReason()
    {
        var elevator = InState(ElevatorState.OutOfService);

        elevator.TakeOutOfService(OutOfServiceReason.Fault);

        elevator.Snapshot().OutOfServiceReason.ShouldBe(OutOfServiceReason.Fault);
    }

    [Fact]
    public void TakeOutOfService_UnknownReason_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => At(1).TakeOutOfService((OutOfServiceReason)99));
    }

    [Fact]
    public void ReturnToService_AfterOutOfService_LeavesTheCarIdleWithTheDoorClosed()
    {
        var elevator = InState(ElevatorState.OutOfService, floor: 6);

        var result = elevator.ReturnToService();

        result.Events.ShouldHaveSingleItem().ShouldBeOfType<ElevatorReturnedToService>().Floor.ShouldBe(new Floor(6));
        var snapshot = elevator.Snapshot();
        snapshot.State.ShouldBe(ElevatorState.Idle);
        snapshot.OutOfServiceReason.ShouldBeNull();
        elevator.AddRequest(Request(Car(2))).Outcome.ShouldBe(AddRequestOutcome.Queued);
    }

    [Theory]
    [InlineData(ElevatorState.Idle)]
    [InlineData(ElevatorState.MovingUp)]
    [InlineData(ElevatorState.DoorOpen)]
    public void ReturnToService_WhenAlreadyInService_IsRejected(ElevatorState state)
    {
        InState(state).ReturnToService().Error.ShouldBe(TransitionError.NotOutOfService);
    }
}
