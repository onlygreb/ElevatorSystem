using CsCheck;
using ElevatorSystem.Application.Occupancy;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ElevatorSystem.Application.Tests.Occupancy;

/// <summary>
/// Property-based tests: thousands of random event sequences, checked after every event. People are
/// conserved (waiting + left behind + riding + present == counted), no count is ever negative, and the
/// car never holds more than its capacity. A failure prints the shrunk sequence and a seed.
/// </summary>
[Collection(CpuHeavyTests.Name)]
public sealed class OccupancyPropertyTests
{
    private const long Iterations = 2_000;
    private static readonly DateTimeOffset Start = TrackedElevator.StartTime;

    private static readonly Gen<int> AnyFloor = Gen.Int[0, 11];

    private static readonly Gen<Step> AnyStep = Gen.Frequency<Step>(
        (5, Gen.Select(AnyFloor, Gen.Enum<Direction>(), (floor, direction) => new Step(StepKind.HallCall, floor, direction))),
        (3, AnyFloor.Select(floor => new Step(StepKind.CarCall, floor))),
        (3, Gen.Const(new Step(StepKind.MoveUp))),
        (3, Gen.Const(new Step(StepKind.MoveDown))),
        (3, Gen.Const(new Step(StepKind.OpenDoor))),
        (2, Gen.Const(new Step(StepKind.CloseDoor))),
        (2, Gen.Int[1, 20].Select(seconds => new Step(StepKind.Wait, seconds))),
        (1, Gen.Enum<OutOfServiceReason>().Select(reason => new Step(StepKind.TakeOutOfService, Reason: reason))),
        (1, Gen.Const(new Step(StepKind.ReturnToService))));

    /// <summary>Events from a real elevator, in the order it produces them.</summary>
    [Fact]
    public void RealElevatorEventSequences_ConservePeopleAndNeverGoNegative()
    {
        Gen.Select(Gen.Int[1, 10], Gen.Int[1, 10], AnyStep.Array[1, 120]).Sample(
            (startFloor, capacity, steps) =>
            {
                var building = TrackedElevator.Create(startFloor, capacity);
                var hallRequests = 0;
                foreach (var step in steps)
                {
                    hallRequests += Apply(building, step);
                    CheckInvariants(building.Tracker, capacity);
                }

                building.Totals.PeopleCounted.ShouldBeLessThanOrEqualTo(hallRequests, "everyone counted pressed a hall button at least once");
            },
            iter: Iterations);
    }

    /// <summary>Arbitrary, even impossible, events: the tracker clamps whatever does not add up.</summary>
    [Fact]
    public void ArbitraryEventSequences_ConservePeopleAndNeverGoNegative()
    {
        Gen.Select(Gen.Int[1, 10], AnyEvent.Array[1, 150]).Sample(
            (capacity, events) =>
            {
                var tracker = new OccupancyTracker(
                    Building.Default, new OccupancyOptions { CarCapacity = capacity }, new FakeTimeProvider(Start), NullLogger<OccupancyTracker>.Instance);
                foreach (var elevatorEvent in events)
                {
                    tracker.Publish(elevatorEvent);
                    CheckInvariants(tracker, capacity);
                }
            },
            iter: Iterations);
    }

    private static Gen<ElevatorEvent> AnyEvent { get; } = Gen.Frequency<ElevatorEvent>(
        (4, Gen.Select(AnyFloor, Gen.Enum<Direction>(), Gen.Int[0, 60], (floor, direction, at) =>
            (ElevatorEvent)new CallQueued(Start.AddSeconds(at), new Floor(floor), Request(new HallCall(new Floor(floor), direction), at), 1))),
        (2, Gen.Select(AnyFloor, Gen.Enum<Direction>(), Gen.Int[0, 60], (floor, direction, at) =>
            (ElevatorEvent)new CallMerged(Start.AddSeconds(at), new Floor(floor), Request(new HallCall(new Floor(floor), direction), at), 1))),
        (3, Gen.Select(AnyFloor, Gen.Int[0, 60], (floor, at) => (ElevatorEvent)new DoorsOpened(Start.AddSeconds(at), new Floor(floor)))),
        (3, Gen.Select(AnyFloor, Gen.Enum<Direction>(), Gen.Int[1, 5], Gen.Int[0, 60], (floor, direction, count, at) =>
            (ElevatorEvent)new CallServed(Start.AddSeconds(at), new Floor(floor), Pending(new HallCall(new Floor(floor), direction), count), TimeSpan.Zero))),
        (3, Gen.Select(AnyFloor, Gen.Int[1, 12], Gen.Int[0, 60], (floor, count, at) =>
            (ElevatorEvent)new CallServed(Start.AddSeconds(at), new Floor(floor), Pending(new CarCall(new Floor(floor)), count), TimeSpan.Zero))),
        (2, AnyFloor.Select(floor => (ElevatorEvent)new DoorsClosed(Start, new Floor(floor)))),
        (2, AnyFloor.Select(floor => (ElevatorEvent)new ElevatorMoved(Start, new Floor(floor), new Floor(floor), Direction.Up))),
        (1, Gen.Select(AnyFloor, Gen.Select(Gen.Int[1, 10], Gen.Enum<Direction>(), Gen.Int[1, 4]).Array[0, 4], (floor, cancelled) =>
            (ElevatorEvent)new ElevatorTakenOutOfService(
                Start,
                new Floor(floor),
                OutOfServiceReason.Fault,
                [.. cancelled.Select(call => Pending(new HallCall(new Floor(call.Item1), call.Item2), call.Item3))]))),
        (1, AnyFloor.Select(floor => (ElevatorEvent)new ElevatorReturnedToService(Start, new Floor(floor)))));

    private static void CheckInvariants(OccupancyTracker tracker, int capacity)
    {
        var totals = tracker.Totals;
        var occupancy = tracker.Current;

        totals.Waiting.ShouldBeGreaterThanOrEqualTo(0);
        totals.LeftBehind.ShouldBeGreaterThanOrEqualTo(0);
        totals.Riding.ShouldBeInRange(0, capacity, "the car never holds more than its capacity");
        totals.Present.ShouldBeGreaterThanOrEqualTo(0);
        (totals.Waiting + totals.LeftBehind + totals.Riding + totals.Present).ShouldBe(totals.PeopleCounted, "people are conserved");
        totals.Riding.ShouldBe(totals.Boarded - totals.Delivered - totals.Evacuated, "riders only leave by arriving or being evacuated");

        occupancy.PassengersInCar.ShouldBe(totals.Riding);
        occupancy.PassengersDelivered.ShouldBe(totals.Delivered);
        occupancy.Floors.ShouldAllBe(floor => floor.Waiting >= 0 && floor.Present >= 0);
        occupancy.Floors.Sum(floor => floor.Waiting).ShouldBe(totals.Waiting + totals.LeftBehind);
        occupancy.Floors.Sum(floor => floor.Present).ShouldBe(totals.Present);
        (occupancy.AverageWaitTime ?? TimeSpan.Zero).ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    /// <returns>How many hall-call requests the elevator accepted.</returns>
    private static int Apply(TrackedElevator building, Step step)
    {
        var elevator = building.Elevator;
        switch (step.Kind)
        {
            case StepKind.HallCall:
            case StepKind.CarCall:
                ElevatorCall call = step.Kind == StepKind.HallCall
                    ? new HallCall(new Floor(step.Value), step.Direction)
                    : new CarCall(new Floor(step.Value));
                var result = elevator.AddRequest(new PassengerRequest(RequestId.New(building.Time), call, building.Time.GetUtcNow()));
                building.Publish(result.Events);
                return call is HallCall && result.Outcome != AddRequestOutcome.Rejected ? 1 : 0;
            case StepKind.MoveUp:
                building.Publish(elevator.MoveUp().Events);
                return 0;
            case StepKind.MoveDown:
                building.Publish(elevator.MoveDown().Events);
                return 0;
            case StepKind.OpenDoor:
                building.OpenDoor();
                return 0;
            case StepKind.CloseDoor:
                building.CloseDoor();
                return 0;
            case StepKind.Wait:
                building.Wait(TimeSpan.FromSeconds(step.Value));
                return 0;
            case StepKind.TakeOutOfService:
                building.TakeOutOfService(step.Reason);
                return 0;
            case StepKind.ReturnToService:
                building.ReturnToService();
                return 0;
            default:
                throw new ArgumentOutOfRangeException(nameof(step), step, null);
        }
    }

    private static PassengerRequest Request(ElevatorCall call, int atSecond) =>
        new(new RequestId(Guid.CreateVersion7(Start.AddSeconds(atSecond))), call, Start.AddSeconds(atSecond));

    private static PendingCall Pending(ElevatorCall call, int requestCount) =>
        new(call, new RequestId(Guid.CreateVersion7(Start)), Start, requestCount);

    internal enum StepKind
    {
        HallCall,
        CarCall,
        MoveUp,
        MoveDown,
        OpenDoor,
        CloseDoor,
        Wait,
        TakeOutOfService,
        ReturnToService,
    }

    internal sealed record Step(
        StepKind Kind,
        int Value = 0,
        Direction Direction = Direction.Up,
        OutOfServiceReason Reason = OutOfServiceReason.Maintenance)
    {
        public override string ToString() => Kind switch
        {
            StepKind.HallCall => $"HallCall({Value}, {Direction})",
            StepKind.CarCall => $"CarCall({Value})",
            StepKind.Wait => $"Wait({Value} s)",
            StepKind.TakeOutOfService => $"TakeOutOfService({Reason})",
            _ => Kind.ToString(),
        };
    }
}
