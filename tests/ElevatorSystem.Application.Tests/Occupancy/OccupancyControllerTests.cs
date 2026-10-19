using ElevatorSystem.Application.Occupancy;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ElevatorSystem.Application.Tests.Occupancy;

/// <summary>The tracker fed by a running controller through the composite sink, in simulated time.</summary>
public sealed class OccupancyControllerTests
{
    [Fact]
    public async Task Journey_CallRideAndArrive_CountsEachStage()
    {
        var tracker = NewTracker();
        await using var harness = ControllerHarness.Create(sink: probe => Feed(tracker, probe)).Start();

        await harness.CallElevatorAsync(7, Direction.Down);
        tracker.Current.Floors[6].Waiting.ShouldBe(1);

        await harness.AdvanceUntilAsync<DoorsOpened>(opened => opened.Floor.Number == 7);
        tracker.Current.Floors[6].Waiting.ShouldBe(0);
        tracker.Current.PassengersInCar.ShouldBe(1);
        tracker.Current.AverageWaitTime.ShouldBe(6 * ControllerHarness.Step, "six one-step trips from floor 1 to floor 7");

        await harness.SelectFloorAsync(2);
        await harness.AdvanceUntilAsync<CallServed>(served => served.Call.Call == new CarCall(new Floor(2)));
        var arrived = tracker.Current;
        arrived.PassengersInCar.ShouldBe(0);
        arrived.Floors[1].Present.ShouldBe(1);
        arrived.PassengersDelivered.ShouldBe(1);
    }

    [Fact]
    public async Task HallCallWhileTheDoorIsOpenThere_IsServedOnTheSpotAndBoards()
    {
        var tracker = NewTracker();
        await using var harness = ControllerHarness.Create(startFloor: 3, sink: probe => Feed(tracker, probe)).Start();
        await harness.CallElevatorAsync(3, Direction.Up);
        await harness.NextAsync<DoorsOpened>();

        await harness.CallElevatorAsync(3, Direction.Down);

        tracker.Current.PassengersInCar.ShouldBe(2);
        tracker.Current.AverageWaitTime.ShouldBe(TimeSpan.Zero);
        tracker.Totals.Corrections.ShouldBe(0);
    }

    [Fact]
    public async Task EmergencyStopMidTrip_EvacuatesRidersAtTheFloorBeingTravelledTo()
    {
        var tracker = NewTracker();
        await using var harness = ControllerHarness.Create(sink: probe => Feed(tracker, probe)).Start();
        await harness.CallElevatorAsync(1, Direction.Up);
        await harness.CallElevatorAsync(5, Direction.Up);
        await harness.NextAsync<DoorsOpened>();
        await harness.SelectFloorAsync(9);
        await harness.AdvanceUntilAsync<ElevatorMoved>(moved => moved.Floor.Number == 4);

        harness.Controller.EmergencyStop();

        var occupancy = tracker.Current;
        occupancy.PassengersInCar.ShouldBe(0);
        occupancy.Floors[3].Present.ShouldBe(1);
        occupancy.Floors[4].Waiting.ShouldBe(1, "the person on floor 5 keeps waiting, their call cancelled");
        tracker.Totals.LeftBehind.ShouldBe(1);
        tracker.Totals.Evacuated.ShouldBe(1);
    }

    [Fact]
    public async Task FailingSinkBeforeTheTracker_DoesNotStopTheCounting()
    {
        var tracker = NewTracker();
        var log = new RecordingLogger<CompositeElevatorEventSink>();
        await using var harness = ControllerHarness.Create(
            sink: probe => new CompositeElevatorEventSink([new ThrowingSink(new EventProbe()), tracker, probe], log)).Start();

        await harness.CallElevatorAsync(4, Direction.Up);
        await harness.AdvanceUntilAsync<DoorsOpened>(opened => opened.Floor.Number == 4);

        tracker.Current.PassengersInCar.ShouldBe(1);
        log.WithId(1806).ShouldNotBeEmpty();
    }

    private static OccupancyTracker NewTracker() => new(
        Building.Default, new OccupancyOptions(), new FakeTimeProvider(ControllerHarness.StartTime), NullLogger<OccupancyTracker>.Instance);

    /// <summary>The tracker first and the probe last, so a test that sees an event knows it was already counted.</summary>
    private static CompositeElevatorEventSink Feed(OccupancyTracker tracker, EventProbe probe) =>
        new([tracker, probe], NullLogger<CompositeElevatorEventSink>.Instance);
}
