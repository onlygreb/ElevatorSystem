using System.Collections.Immutable;
using ElevatorSystem.Application.Occupancy;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ElevatorSystem.Application.Tests.Occupancy;

/// <summary>
/// The counting rules of ADR-0017 §2 and ADR-0020, one per test, on events produced by a real domain
/// elevator driven by hand.
/// </summary>
public sealed class OccupancyTrackerTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Fact]
    public void Publish_HallCallQueued_CountsOnePersonWaitingOnItsFloor()
    {
        var building = TrackedElevator.Create();

        building.Call(4, Direction.Up);

        building.Floor(4).Waiting.ShouldBe(1);
        building.Totals.ShouldBe(new OccupancyTotals(PeopleCounted: 1, Waiting: 1, 0, 0, 0, 0, 0, 0, 0));
    }

    [Fact]
    public void Publish_HallCallMerged_CountsEveryPressAsOnePerson()
    {
        var building = TrackedElevator.Create();

        building.Call(4, Direction.Up).Call(4, Direction.Up).Call(4, Direction.Up);

        building.Elevator.Snapshot().PendingCalls.ShouldHaveSingleItem().RequestCount.ShouldBe(3);
        building.Floor(4).Waiting.ShouldBe(3, "each press is one person (ADR-0017)");
        building.Totals.PeopleCounted.ShouldBe(3);
    }

    [Fact]
    public void Publish_HallCallFromAFloorWithPeoplePresent_MovesOneOfThemToWaiting()
    {
        var building = TrackedElevator.Create();
        building.Call(1).OpenDoor().Select(5).StopAt(5);
        building.Floor(5).Present.ShouldBe(1);

        building.CloseDoor().Call(5, Direction.Down);

        building.Floor(5).Present.ShouldBe(0);
        building.Floor(5).Waiting.ShouldBe(1);
        building.Totals.PeopleCounted.ShouldBe(1, "the person who arrived at floor 5 is the one calling from it");
    }

    [Fact]
    public void Publish_DoorsOpen_BoardsEveryoneWaitingThereWhicheverWayTheyGo()
    {
        var building = TrackedElevator.Create();
        building.Call(6, Direction.Up).Call(6, Direction.Down).Call(3, Direction.Up);

        building.StopAt(6);

        building.Floor(6).Waiting.ShouldBe(0);
        building.Floor(3).Waiting.ShouldBe(1, "the car passed floor 3 without stopping");
        building.Current.PassengersInCar.ShouldBe(2);
        building.Totals.Boarded.ShouldBe(2);
    }

    [Fact]
    public void Publish_DoorsOpenWithMoreWaitingThanRoom_BoardsInArrivalOrderUpToCapacity()
    {
        var building = TrackedElevator.Create(capacity: 2);
        building.Call(5, Direction.Up).Wait(2 * Second)
            .Call(5, Direction.Up).Wait(2 * Second)
            .Call(5, Direction.Down).Wait(6 * Second);

        building.StopAt(5);

        var occupancy = building.Current;
        occupancy.PassengersInCar.ShouldBe(2);
        occupancy.CarCapacity.ShouldBe(2);
        occupancy.AverageWaitTime.ShouldBe(9 * Second, "the first two callers boarded, after 10 s and 8 s");
        building.Floor(5).Waiting.ShouldBe(1);
        building.Totals.LeftBehind.ShouldBe(1, "the last caller's call was served while the car was full");
        building.Totals.Waiting.ShouldBe(0);
    }

    [Fact]
    public void Publish_LeftBehindPersonCallsAgain_IsTheSamePersonNotANewOne()
    {
        var building = TrackedElevator.Create(capacity: 1);
        building.Call(5, Direction.Up).Call(5, Direction.Up).StopAt(5).CloseDoor();
        building.Totals.LeftBehind.ShouldBe(1);

        building.Call(5, Direction.Up);

        building.Floor(5).Waiting.ShouldBe(1);
        building.Totals.ShouldBe(new OccupancyTotals(
            PeopleCounted: 2, Waiting: 1, LeftBehind: 0, Riding: 1, Present: 0, Delivered: 0, Evacuated: 0, Boarded: 1, Corrections: 0));
    }

    [Fact]
    public void Publish_LeftBehindPersonAfterRoomIsFreed_BoardsAtTheNextStop()
    {
        var building = TrackedElevator.Create(capacity: 1);
        building.Call(5, Direction.Up).Call(5, Direction.Up).StopAt(5).Select(8);
        building.TravelTo(8).OpenDoor().CloseDoor().Call(5, Direction.Up);

        building.StopAt(5);

        building.Floor(5).Waiting.ShouldBe(0);
        building.Current.PassengersInCar.ShouldBe(1);
        building.Current.PassengersDelivered.ShouldBe(1);
        building.Totals.PeopleCounted.ShouldBe(2);
    }

    [Fact]
    public void Publish_CarCallServed_LetsThatManyRidersOffAsDelivered()
    {
        var building = TrackedElevator.Create();
        building.Call(1).Call(1).Call(1).OpenDoor().Select(7).Select(7).Select(9);

        building.StopAt(7);

        building.Current.PassengersInCar.ShouldBe(1);
        building.Floor(7).Present.ShouldBe(2, "two riders chose floor 7 (a merged car call)");
        building.Current.PassengersDelivered.ShouldBe(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Publish_StopWhereRidersGetOffAndOthersWait_LetsRidersOffBeforeOthersBoard(bool hallCallFirst)
    {
        var building = TrackedElevator.Create(capacity: 2);
        building.Call(1).Call(1).OpenDoor();
        if (hallCallFirst)
        {
            building.Call(5, Direction.Down).Call(5, Direction.Down).Select(5).Select(5);
        }
        else
        {
            building.Select(5).Select(5).Call(5, Direction.Down).Call(5, Direction.Down);
        }

        building.StopAt(5);

        building.Current.PassengersInCar.ShouldBe(2, "the two riders made room for the two waiting");
        building.Floor(5).Waiting.ShouldBe(0);
        building.Floor(5).Present.ShouldBe(2);
        building.Totals.Corrections.ShouldBe(0);
    }

    [Fact]
    public void Publish_HallCallServedOnTheSpot_BoardsAtOnceWithNoWait()
    {
        var building = TrackedElevator.Create(startFloor: 3);
        building.OpenDoor();

        building.Call(3, Direction.Up);

        building.Current.PassengersInCar.ShouldBe(1);
        building.Current.AverageWaitTime.ShouldBe(TimeSpan.Zero);
        building.Floor(3).Waiting.ShouldBe(0);
        building.Totals.Corrections.ShouldBe(0);
        building.Log.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Publish_HallCallServedOnTheSpotWhileTheCarIsFull_LeavesThePersonBehind()
    {
        var building = TrackedElevator.Create(startFloor: 3, capacity: 1);
        building.Call(3).OpenDoor();

        building.Call(3, Direction.Down);

        building.Current.PassengersInCar.ShouldBe(1);
        building.Floor(3).Waiting.ShouldBe(1);
        building.Totals.LeftBehind.ShouldBe(1, "the call was served at once, but there was no room");
    }

    [Fact]
    public void Publish_CarCallServedOnTheSpot_LetsOneRiderOff()
    {
        var building = TrackedElevator.Create(startFloor: 3);
        building.Call(3).Call(3).OpenDoor();

        building.Select(3);

        building.Current.PassengersInCar.ShouldBe(1);
        building.Floor(3).Present.ShouldBe(1);
        building.Current.PassengersDelivered.ShouldBe(1);
    }

    [Fact]
    public void Publish_CarCallWithNobodyRiding_IsClampedAndLogged()
    {
        var building = TrackedElevator.Create();
        building.Select(6);

        building.StopAt(6);

        building.Current.PassengersInCar.ShouldBe(0);
        building.Current.PassengersDelivered.ShouldBe(0);
        building.Floor(6).Present.ShouldBe(0, "nobody was riding, so nobody got off");
        building.Totals.Corrections.ShouldBe(1);
        var entry = building.Log.WithId(1800).ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain("floor 6");
    }

    [Fact]
    public void Publish_TakenOutOfService_EvacuatesRidersAtTheCarsFloor()
    {
        var building = TrackedElevator.Create();
        building.Call(1).Call(1).Call(1).OpenDoor().Select(8).TravelTo(6);

        building.TakeOutOfService(OutOfServiceReason.EmergencyStop);

        building.Current.PassengersInCar.ShouldBe(0);
        building.Floor(6).Present.ShouldBe(3, "the riders got off where the car stopped");
        building.Current.PassengersDelivered.ShouldBe(0, "they did not reach the floor they chose");
        building.Totals.Evacuated.ShouldBe(3);
        building.Log.WithId(1804).ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Information);
    }

    [Fact]
    public void Publish_TakenOutOfService_KeepsPeopleWaitingButTheyMustCallAgain()
    {
        var building = TrackedElevator.Create();
        building.Call(4, Direction.Up).Call(4, Direction.Up).Call(9, Direction.Down);

        building.TakeOutOfService(OutOfServiceReason.Maintenance);

        building.Floor(4).Waiting.ShouldBe(2);
        building.Floor(9).Waiting.ShouldBe(1);
        building.Totals.Waiting.ShouldBe(0, "every call was cancelled");
        building.Totals.LeftBehind.ShouldBe(3);
    }

    [Fact]
    public void Publish_CallAgainAfterReturnToService_CountsNobodyNew()
    {
        var building = TrackedElevator.Create();
        building.Call(4, Direction.Up).TakeOutOfService().ReturnToService();

        building.Call(4, Direction.Up).StopAt(4);

        building.Current.PassengersInCar.ShouldBe(1);
        building.Totals.PeopleCounted.ShouldBe(1);
        building.Current.AverageWaitTime.ShouldBe(TimeSpan.Zero, "no time passed in this script");
    }

    [Fact]
    public void Publish_StopOvertakenByASafetyCommand_MovesNobody()
    {
        var building = TrackedElevator.Create();
        building.Call(1).OpenDoor().Select(3).Call(3, Direction.Down).TravelTo(3);
        var overtaken = building.Elevator.OpenDoor().Events;

        building.TakeOutOfService().Publish(overtaken);

        building.Floor(3).Present.ShouldBe(1, "the rider was evacuated at floor 3");
        building.Floor(3).Waiting.ShouldBe(1, "nobody boards an out-of-service car");
        building.Current.PassengersDelivered.ShouldBe(0);
        building.Totals.Corrections.ShouldBe(0);
        building.Totals.PeopleCounted.ShouldBe(2);
    }

    [Fact]
    public void Publish_HallCallServedWithRequestsNeverSeen_CountsThemAndLogs()
    {
        var building = TrackedElevator.Create();
        var restored = new PendingCall(new HallCall(new Floor(2), Direction.Up), RequestId.New(building.Time), building.Time.GetUtcNow(), 2);

        building.Publish([new DoorsOpened(building.Time.GetUtcNow(), new Floor(2)), new CallServed(building.Time.GetUtcNow(), new Floor(2), restored, TimeSpan.Zero)]);

        building.Current.PassengersInCar.ShouldBe(2);
        building.Totals.PeopleCounted.ShouldBe(2);
        building.Totals.Corrections.ShouldBe(1);
        building.Log.WithId(1801).ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Warning);
    }

    [Fact]
    public void Publish_OutOfServiceCancellingRequestsNeverSeen_CountsThemAsWaiting()
    {
        var building = TrackedElevator.Create();
        var restored = new PendingCall(new HallCall(new Floor(7), Direction.Down), RequestId.New(building.Time), building.Time.GetUtcNow(), 3);

        building.Publish([new ElevatorTakenOutOfService(building.Time.GetUtcNow(), new Floor(1), OutOfServiceReason.Fault, [restored])]);

        building.Floor(7).Waiting.ShouldBe(3);
        building.Totals.LeftBehind.ShouldBe(3);
        building.Log.WithId(1801).ShouldHaveSingleItem();
    }

    [Fact]
    public void Publish_EventForAFloorOutsideTheBuilding_IsIgnoredAndLogged()
    {
        var building = TrackedElevator.Create();

        building.Publish([new DoorsOpened(building.Time.GetUtcNow(), new Floor(42))]);

        building.Totals.ShouldBe(default);
        building.Log.WithId(1803).ShouldHaveSingleItem();
    }

    [Fact]
    public void Current_BeforeAnyone_ListsEveryFloorLowestFirstWithNoAverageWait()
    {
        var building = TrackedElevator.Create(capacity: 5);

        var occupancy = building.Current;

        occupancy.Floors.Select(floor => floor.Floor).ShouldBe(Enumerable.Range(1, 10));
        occupancy.Floors.ShouldAllBe(floor => floor.Waiting == 0 && floor.Present == 0);
        occupancy.CarCapacity.ShouldBe(5);
        occupancy.PassengersInCar.ShouldBe(0);
        occupancy.AverageWaitTime.ShouldBeNull();
        occupancy.TakenAt.ShouldBe(TrackedElevator.StartTime);
    }

    [Fact]
    public void Observer_IsToldEveryTimeTheCountsChangeAndOnlyThen()
    {
        var observer = new RecordingObserver();
        var building = TrackedElevator.Create(capacity: 1, observer: observer);

        building.Call(2).Call(2);
        observer.Changes.Count.ShouldBe(2);
        building.StopAt(2);
        observer.Changes.Count.ShouldBe(3, "one boarded; moving and the served call changed nothing else");
        building.CloseDoor().Call(2);
        observer.Changes.Count.ShouldBe(3, "the person left behind calling again changes no count");

        observer.Changes[^1].PassengersInCar.ShouldBe(1);
        observer.Changes[^1].Floors[1].Waiting.ShouldBe(1);
    }

    [Fact]
    public void Observer_ThatThrows_IsLoggedAndCountingContinues()
    {
        var building = TrackedElevator.Create(observer: new ThrowingObserver());

        building.Call(2).Call(3);

        building.Totals.PeopleCounted.ShouldBe(2);
        building.Log.WithId(1805).Count.ShouldBe(2);
    }

    [Fact]
    public void Constructor_CapacityBelowOne_IsRefused()
    {
        var time = new FakeTimeProvider();

        Should.Throw<ArgumentOutOfRangeException>(() => new OccupancyTracker(
            Building.Default, new OccupancyOptions { CarCapacity = 0 }, time, NullLogger<OccupancyTracker>.Instance));
    }

    [Fact]
    public async Task Publish_FromManyThreadsAtOnce_LosesNoPerson()
    {
        var time = new FakeTimeProvider(TrackedElevator.StartTime);
        var tracker = new OccupancyTracker(Building.Default, new OccupancyOptions(), time, NullLogger<OccupancyTracker>.Instance);
        const int Threads = 8;
        const int CallsPerThread = 2_000;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishers = Enumerable.Range(0, Threads)
            .Select(thread => Task.Run(
                async () =>
                {
                    await start.Task;
                    for (var index = 0; index < CallsPerThread; index++)
                    {
                        var call = new HallCall(new Floor(2 + ((thread + index) % 8)), Direction.Up);
                        var request = new PassengerRequest(RequestId.New(time), call, time.GetUtcNow());
                        tracker.Publish(new CallMerged(time.GetUtcNow(), call.Floor, request, 1));
                        _ = tracker.Current;
                    }
                },
                TestContext.Current.CancellationToken))
            .ToArray();

        start.SetResult();
        await Task.WhenAll(publishers);

        tracker.Totals.PeopleCounted.ShouldBe(Threads * CallsPerThread);
        tracker.Current.Floors.Sum(floor => floor.Waiting).ShouldBe(Threads * CallsPerThread);
    }

    [Fact]
    public void Publish_OutOfServiceWithNoCalls_ReportsNoCancelledPeople()
    {
        var building = TrackedElevator.Create();

        building.Publish([new ElevatorTakenOutOfService(building.Time.GetUtcNow(), new Floor(1), OutOfServiceReason.Maintenance, ImmutableArray<PendingCall>.Empty)]);

        building.Totals.ShouldBe(default);
        building.Log.Entries.ShouldBeEmpty("nobody had to be evacuated or told to call again");
    }
}
