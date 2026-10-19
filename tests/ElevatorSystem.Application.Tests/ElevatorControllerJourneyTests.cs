using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;
using ElevatorSystem.Domain.Scheduling;

namespace ElevatorSystem.Application.Tests;

/// <summary>Whole journeys through the control loop, in simulated time, asserting the exact sequence of events.</summary>
public sealed class ElevatorControllerJourneyTests
{
    private static readonly TimeSpan Step = ControllerHarness.Step;

    [Fact]
    public async Task ProcessRequests_HallCallThenCarCall_TakesThePassengerWhereTheyAsked()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1).Start();

        await harness.CallElevatorAsync(7, Direction.Down);
        var pickup = await harness.AdvanceUntilAsync<DoorsOpened>();
        var served = await harness.NextAsync<CallServed>();
        await harness.SelectFloorAsync(2);
        var rest = await harness.RunUntilIdleAsync();

        pickup.Floor.ShouldBe(new Floor(7));
        served.Call.Call.ShouldBe(new HallCall(new Floor(7), Direction.Down));
        served.WaitTime.ShouldBe(Step * 6, "six floors at one step each");
        Stops(rest).ShouldBe([new Floor(2)]);
        Moves(rest).ShouldBe([6, 5, 4, 3, 2]);
        harness.Elevator.CurrentFloor.ShouldBe(new Floor(2));
    }

    [Fact]
    public async Task ProcessRequests_Fifo_ServesFloorsInArrivalOrderWithoutStoppingOnTheWay()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1).Start();

        await harness.SelectFloorAsync(5);
        await harness.SelectFloorAsync(3);
        await harness.SelectFloorAsync(8);
        var events = await harness.RunUntilIdleAsync();

        Stops(events).ShouldBe([new Floor(5), new Floor(3), new Floor(8)]);
        Moves(events).ShouldBe([2, 3, 4, 5, 4, 3, 4, 5, 6, 7, 8], "the car passes floor 3 on its way to 5 without stopping");
    }

    [Fact]
    public async Task ProcessRequests_Look_StopsAtPendingFloorsOnTheWay()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1, strategy: new LookSchedulingStrategy()).Start();

        await harness.SelectFloorAsync(5);
        await harness.SelectFloorAsync(3);
        await harness.SelectFloorAsync(8);
        var events = await harness.RunUntilIdleAsync();

        Stops(events).ShouldBe([new Floor(3), new Floor(5), new Floor(8)]);
        Moves(events).ShouldBe([2, 3, 4, 5, 6, 7, 8]);
    }

    [Fact]
    public async Task ProcessRequests_RepeatedCalls_AreMergedAndServedOnce()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1).Start();

        await harness.CallElevatorAsync(6, Direction.Up);
        await harness.CallElevatorAsync(6, Direction.Up);
        await harness.CallElevatorAsync(6, Direction.Up);
        var events = await harness.RunUntilIdleAsync();

        harness.Probe.Events.OfType<CallQueued>().ShouldHaveSingleItem();
        harness.Probe.Events.OfType<CallMerged>().Count().ShouldBe(2);
        events.OfType<CallServed>().ShouldHaveSingleItem().Call.RequestCount.ShouldBe(3);
        Stops(events).ShouldBe([new Floor(6)]);
    }

    [Fact]
    public async Task ProcessRequests_RequestDuringATrip_IsAssignedWithoutWaitingForTheCar()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1).Start();
        await harness.SelectFloorAsync(9);
        await harness.NextAsync<ElevatorMoved>();

        var result = harness.Controller.RequestElevator(4, Direction.Up);
        var queued = await harness.NextAsync<CallQueued>(e => e.Request.Id == result.RequestId);

        queued.OccurredAt.ShouldBe(queued.Request.RequestedAt, "assignment happens while the car is still travelling");
        harness.Elevator.State.ShouldBe(ElevatorState.MovingUp);
        queued.Position.ShouldBe(2);
    }

    [Fact]
    public async Task ProcessRequests_CallAtTheCurrentFloor_OpensTheDoorAtOnce()
    {
        await using var harness = ControllerHarness.Create(startFloor: 3).Start();

        await harness.CallElevatorAsync(3, Direction.Up);
        var opened = await harness.NextAsync<DoorsOpened>();
        var served = await harness.NextAsync<CallServed>();

        opened.Floor.ShouldBe(new Floor(3));
        opened.OccurredAt.ShouldBe(ControllerHarness.StartTime);
        served.WaitTime.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task ProcessRequests_DoorStaysOpenForTheConfiguredDuration()
    {
        await using var harness = ControllerHarness.Create(startFloor: 3).Start();

        await harness.CallElevatorAsync(3, Direction.Down);
        var opened = await harness.NextAsync<DoorsOpened>();
        var closed = await harness.AdvanceUntilAsync<DoorsClosed>();

        (closed.OccurredAt - opened.OccurredAt).ShouldBe(ControllerHarness.DefaultOptions.DoorOpenDuration);
        harness.Elevator.State.ShouldBe(ElevatorState.Idle);
    }

    [Fact]
    public async Task ProcessRequests_CallForTheOpenDoorsFloor_IsServedWithoutAnotherStop()
    {
        await using var harness = ControllerHarness.Create(startFloor: 4).Start();
        await harness.SelectFloorAsync(4);
        await harness.NextAsync<DoorsOpened>();

        await harness.CallElevatorAsync(4, Direction.Up);
        var events = await harness.RunUntilIdleAsync();

        harness.Probe.Events.OfType<CallServed>().Count().ShouldBe(2);
        harness.Probe.Events.OfType<DoorsOpened>().ShouldHaveSingleItem();
        events.OfType<ElevatorMoved>().ShouldBeEmpty();
    }

    private static List<Floor> Stops(IEnumerable<ElevatorEvent> events) =>
        [.. events.OfType<DoorsOpened>().Select(e => e.Floor)];

    private static List<int> Moves(IEnumerable<ElevatorEvent> events) =>
        [.. events.OfType<ElevatorMoved>().Select(e => e.Floor.Number)];
}
