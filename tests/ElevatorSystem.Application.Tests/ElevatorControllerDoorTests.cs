using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;

namespace ElevatorSystem.Application.Tests;

/// <summary>
/// The default door policy (ADR-0025): with nowhere to go, the car waits with its door open; a call that gives it
/// somewhere to go starts the door countdown, and the status says when the door will close.
/// </summary>
public sealed class ElevatorControllerDoorTests
{
    private static readonly ElevatorControllerOptions HoldOpen = ControllerHarness.DefaultOptions with { HoldDoorOpenWhenIdle = true };

    [Fact]
    public async Task Start_WithNothingToDo_OpensTheDoorAndWaitsWithoutACountdown()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1, options: HoldOpen).Start();

        var opened = await harness.NextAsync<DoorsOpened>();
        await harness.StepsAsync(10);

        opened.Floor.ShouldBe(new Floor(1));
        harness.Elevator.State.ShouldBe(ElevatorState.DoorOpen);
        harness.Controller.DoorsCloseAt.ShouldBeNull();
        harness.Probe.Events.OfType<DoorsClosed>().ShouldBeEmpty("an idle car keeps its door open");
    }

    [Fact]
    public async Task ACallElsewhere_StartsTheCountdown_ThenTheCarLeaves()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1, options: HoldOpen).Start();
        await harness.NextAsync<DoorsOpened>();

        await harness.CallElevatorAsync(5, Direction.Down);

        harness.Controller.DoorsCloseAt.ShouldBe(harness.Time.GetUtcNow() + HoldOpen.DoorOpenDuration);
        var closed = await harness.AdvanceUntilAsync<DoorsClosed>();
        closed.OccurredAt.ShouldBe(ControllerHarness.StartTime + HoldOpen.DoorOpenDuration);
        await harness.AdvanceUntilAsync<ElevatorMoved>();
        harness.Controller.DoorsCloseAt.ShouldBeNull("the door is closed while travelling");
    }

    [Fact]
    public async Task ArrivingWithNothingElsePending_WaitsOpenThere()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1, options: HoldOpen).Start();
        await harness.NextAsync<DoorsOpened>();
        await harness.SelectFloorAsync(4);

        var arrived = await harness.AdvanceUntilAsync<DoorsOpened>(opened => opened.Floor == new Floor(4));
        await harness.StepsAsync(10);

        arrived.Floor.ShouldBe(new Floor(4));
        harness.Elevator.State.ShouldBe(ElevatorState.DoorOpen);
        harness.Controller.DoorsCloseAt.ShouldBeNull();
        harness.Probe.Events.OfType<DoorsClosed>().Count().ShouldBe(1, "only the door at floor 1 closed, to leave");
    }

    [Fact]
    public async Task ArrivingWithMoreToServe_CountsDownAndCarriesOn()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1, options: HoldOpen).Start();
        await harness.NextAsync<DoorsOpened>();
        await harness.SelectFloorAsync(3);
        await harness.SelectFloorAsync(6);

        await harness.AdvanceUntilAsync<DoorsOpened>(opened => opened.Floor == new Floor(3));

        harness.Controller.DoorsCloseAt.ShouldBe(harness.Time.GetUtcNow() + HoldOpen.DoorOpenDuration);
        await harness.AdvanceUntilAsync<DoorsOpened>(opened => opened.Floor == new Floor(6));
        harness.Controller.DoorsCloseAt.ShouldBeNull();
    }

    [Fact]
    public async Task ACallForTheOpenFloor_IsServedAndTheCarKeepsWaiting()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1, options: HoldOpen).Start();
        await harness.NextAsync<DoorsOpened>();

        await harness.CallElevatorAsync(1, Direction.Up);
        await harness.StepsAsync(5);

        harness.Probe.Events.OfType<CallServed>().ShouldHaveSingleItem();
        harness.Probe.Events.OfType<DoorsClosed>().ShouldBeEmpty();
        harness.Controller.DoorsCloseAt.ShouldBeNull();
    }

    [Fact]
    public async Task ReturnToService_OpensTheDoorAndWaits()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1, options: HoldOpen).Start();
        await harness.NextAsync<DoorsOpened>();
        harness.Controller.StartMaintenance();

        harness.Controller.ReturnToService().Succeeded.ShouldBeTrue();

        await harness.Probe.WaitForAsync<DoorsOpened>(opened => opened.OccurredAt >= ControllerHarness.StartTime, 1);
        harness.Elevator.State.ShouldBe(ElevatorState.DoorOpen);
    }

    [Fact]
    public async Task Status_CarriesTheCountdown_OnlyWhileTheDoorIsOpen()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1, options: HoldOpen).Start();
        await harness.NextAsync<DoorsOpened>();
        await harness.CallElevatorAsync(5, Direction.Down);

        var counting = ElevatorContractMapper.ToStatus(harness.Elevator.Snapshot(), harness.Controller.DoorsCloseAt);
        var closedSnapshot = new Elevator(Building.Default, TimeProvider.System, new Floor(2)).Snapshot();

        counting.Status.ShouldBe(ElevatorStatus.DoorOpen);
        counting.DoorsCloseAt.ShouldBe(ControllerHarness.StartTime + HoldOpen.DoorOpenDuration);
        ElevatorContractMapper.ToStatus(closedSnapshot, ControllerHarness.StartTime).DoorsCloseAt.ShouldBeNull("a closed door has no countdown");
    }
}
