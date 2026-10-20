using System.Collections.Immutable;
using CsCheck;
using ElevatorSystem.Domain.Events;
using static ElevatorSystem.Domain.Tests.TestElevators;

namespace ElevatorSystem.Domain.Tests;

/// <summary>
/// Property-based tests: thousands of random command sequences, including floors outside the
/// building and impossible directions, with the elevator's invariants checked after every step.
/// A failure prints the shrunk sequence and a seed to turn into a regression test.
/// </summary>
public sealed class ElevatorInvariantTests
{
    private const long Iterations = 2_000;

    private static readonly Gen<int> AnyFloor = Gen.Int[-1, 12];

    private static readonly Gen<Step> AnyStep = Gen.Frequency<Step>(
        (3, Gen.Select(AnyFloor, Gen.Enum<Direction>(), (floor, direction) => new Step(StepKind.HallCall, floor, direction))),
        (3, AnyFloor.Select(floor => new Step(StepKind.CarCall, floor))),
        (3, Gen.Const(new Step(StepKind.MoveUp))),
        (3, Gen.Const(new Step(StepKind.MoveDown))),
        (2, Gen.Const(new Step(StepKind.OpenDoor))),
        (2, Gen.Const(new Step(StepKind.CloseDoor))),
        (1, Gen.Enum<OutOfServiceReason>().Select(reason => new Step(StepKind.TakeOutOfService, Reason: reason))),
        (1, Gen.Const(new Step(StepKind.ReturnToService))),
        (1, AnyFloor.Select(floor => new Step(StepKind.ClearRequests, floor))));

    private static readonly Gen<(int StartFloor, Step[] Steps)> AnyScenario = Gen.Select(Gen.Int[1, 10], AnyStep.Array[1, 80]);

    [Fact]
    public void RandomCommandSequences_NeverBreakTheElevatorInvariants()
    {
        AnyScenario.Sample(
            (startFloor, steps) =>
            {
                var elevator = At(startFloor);
                foreach (var step in steps)
                {
                    var before = elevator.Snapshot();
                    Apply(elevator, step);
                    CheckInvariants(before, elevator.Snapshot(), step);
                }
            },
            iter: Iterations);
    }

    [Fact]
    public void RandomCommandSequences_AccountForEveryAcceptedRequest()
    {
        AnyScenario.Sample(
            (startFloor, steps) =>
            {
                var elevator = At(startFloor);
                int accepted = 0, served = 0, cancelled = 0;
                foreach (var step in steps)
                {
                    var (wasAccepted, events) = Apply(elevator, step);
                    accepted += wasAccepted ? 1 : 0;
                    served += events.OfType<CallServed>().Sum(e => e.Call.RequestCount);
                    cancelled += events.OfType<ElevatorTakenOutOfService>().Sum(e => e.CancelledCalls.Sum(call => call.RequestCount));
                    cancelled += events.OfType<RequestsCleared>().Sum(e => e.CancelledCalls.Sum(call => call.RequestCount));
                }

                var pending = elevator.Snapshot().PendingCalls.Sum(call => call.RequestCount);
                (pending + served + cancelled).ShouldBe(accepted, "every accepted request is pending, served or cancelled - exactly once");
            },
            iter: Iterations);
    }

    private static void CheckInvariants(ElevatorSnapshot before, ElevatorSnapshot after, Step step)
    {
        Building.Default.Contains(after.CurrentFloor).ShouldBeTrue("the car never leaves the building");

        var floorChange = after.CurrentFloor.Number - before.CurrentFloor.Number;
        if (floorChange != 0)
        {
            step.Kind.ShouldBeOneOf(StepKind.MoveUp, StepKind.MoveDown);
            Math.Abs(floorChange).ShouldBe(1, "a move covers exactly one floor");
            before.State.ShouldNotBe(ElevatorState.DoorOpen, "the car never moves with its door open");
            before.State.ShouldNotBe(ElevatorState.OutOfService, "the car never moves while out of service");
            after.State.ShouldBe(floorChange > 0 ? ElevatorState.MovingUp : ElevatorState.MovingDown);
        }

        var calls = after.PendingCalls.Select(pending => pending.Call).ToList();
        calls.ShouldBeUnique("repeated calls are merged, never queued twice");
        calls.Count.ShouldBeLessThanOrEqualTo(Building.Default.MaxPendingCalls);
        calls.ShouldAllBe(call => Building.Default.Validate(call) == null, "only valid calls are ever queued");

        if (after.State == ElevatorState.OutOfService)
        {
            calls.ShouldBeEmpty("an out-of-service car holds no calls");
        }

        if (after.State == ElevatorState.DoorOpen)
        {
            calls.ShouldNotContain(call => call.Floor == after.CurrentFloor, "no call waits at a floor whose door is open");
        }

        if (after.Version == before.Version)
        {
            after.CurrentFloor.ShouldBe(before.CurrentFloor, "nothing changes without a new version");
            after.State.ShouldBe(before.State);
            calls.ShouldBe(before.PendingCalls.Select(pending => pending.Call).ToList());
        }
    }

    private static (bool AcceptedRequest, ImmutableArray<ElevatorEvent> Events) Apply(Elevator elevator, Step step)
    {
        switch (step.Kind)
        {
            case StepKind.HallCall:
            case StepKind.CarCall:
                ElevatorCall call = step.Kind == StepKind.HallCall
                    ? new HallCall(new Floor(step.Floor), step.Direction)
                    : new CarCall(new Floor(step.Floor));
                var result = elevator.AddRequest(Request(call));
                return (result.Outcome != AddRequestOutcome.Rejected, result.Events);
            case StepKind.MoveUp:
                return (false, elevator.MoveUp().Events);
            case StepKind.MoveDown:
                return (false, elevator.MoveDown().Events);
            case StepKind.OpenDoor:
                return (false, elevator.OpenDoor().Events);
            case StepKind.CloseDoor:
                return (false, elevator.CloseDoor().Events);
            case StepKind.TakeOutOfService:
                return (false, elevator.TakeOutOfService(step.Reason).Events);
            case StepKind.ReturnToService:
                return (false, elevator.ReturnToService().Events);
            case StepKind.ClearRequests:
                // Keeps whatever waits at one floor, as an admin's clear keeps the calls of people still waiting.
                var keep = elevator.Snapshot().PendingCalls.Select(pending => pending.Call).Where(call => call.Floor.Number == step.Floor).ToList();
                return (false, elevator.ClearRequests(keep).Events);
            default:
                throw new ArgumentOutOfRangeException(nameof(step), step, null);
        }
    }

    internal enum StepKind
    {
        HallCall,
        CarCall,
        MoveUp,
        MoveDown,
        OpenDoor,
        CloseDoor,
        TakeOutOfService,
        ReturnToService,
        ClearRequests,
    }

    internal sealed record Step(
        StepKind Kind,
        int Floor = 0,
        Direction Direction = Direction.Up,
        OutOfServiceReason Reason = OutOfServiceReason.Maintenance)
    {
        public override string ToString() => Kind switch
        {
            StepKind.HallCall => $"HallCall({Floor}, {Direction})",
            StepKind.CarCall => $"CarCall({Floor})",
            StepKind.TakeOutOfService => $"TakeOutOfService({Reason})",
            StepKind.ClearRequests => $"ClearRequests(keep floor {Floor})",
            _ => Kind.ToString(),
        };
    }
}
