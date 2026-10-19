using CsCheck;
using ElevatorSystem.Domain.Scheduling;
using static ElevatorSystem.Domain.Tests.TestElevators;

namespace ElevatorSystem.Domain.Tests.Scheduling;

/// <summary>
/// Whole-workload properties of each strategy over random sets of valid calls, driven to
/// completion with <see cref="SynchronousDriver"/>.
/// </summary>
public sealed class SchedulingPropertyTests
{
    private const long Iterations = 1_000;

    private static readonly Gen<ElevatorCall> AnyValidCall = Gen.Select(
        Gen.Int[1, 10],
        Gen.Enum<Direction>(),
        Gen.Bool,
        (number, direction, isCarCall) =>
        {
            var floor = new Floor(number);
            return isCarCall
                ? new CarCall(floor)
                : (ElevatorCall)new HallCall(floor, Building.Default.CanTravel(floor, direction) ? direction : direction.Opposite());
        });

    private static readonly Gen<(int StartFloor, ElevatorCall[] Calls)> AnyWorkload =
        Gen.Select(Gen.Int[1, 10], AnyValidCall.Array[1, 40]);

    [Fact]
    public void Fifo_StopsAtEachRequestedFloorOnce_InTheOrderFirstRequested()
    {
        AnyWorkload.Sample(
            (startFloor, calls) =>
            {
                var elevator = Loaded(startFloor, calls);
                var expectedStops = elevator.TargetFloors.ToList();

                var run = SynchronousDriver.Drive(elevator, new FifoSchedulingStrategy());

                run.Stops.ShouldBe(expectedStops, "FIFO serves floors strictly in arrival order");
                run.ServedRequests.ShouldBe(calls.Length, "every request is served");
                elevator.Snapshot().PendingCalls.ShouldBeEmpty();
            },
            iter: Iterations);
    }

    [Fact]
    public void Look_ServesEverythingInOneSweepAndAtMostOneReversal()
    {
        AnyWorkload.Sample(
            (startFloor, calls) =>
            {
                var elevator = Loaded(startFloor, calls);
                var requestedFloors = elevator.TargetFloors.ToHashSet();

                var run = SynchronousDriver.Drive(elevator, new LookSchedulingStrategy());

                run.Stops.ShouldBeUnique("LOOK stops at each floor once");
                run.Stops.ToHashSet().SetEquals(requestedFloors).ShouldBeTrue("LOOK stops at every requested floor");
                run.Reversals.ShouldBeLessThanOrEqualTo(1, "a fixed workload needs one sweep and at most one reversal");
                run.ServedRequests.ShouldBe(calls.Length);
            },
            iter: Iterations);
    }

    private static Elevator Loaded(int startFloor, ElevatorCall[] calls)
    {
        var elevator = At(startFloor);
        foreach (var call in calls)
        {
            elevator.AddRequest(Request(call)).Outcome.ShouldBeOneOf(AddRequestOutcome.Queued, AddRequestOutcome.Merged);
        }

        return elevator;
    }
}
