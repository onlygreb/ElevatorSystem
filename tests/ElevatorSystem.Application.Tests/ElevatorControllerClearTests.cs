using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;

namespace ElevatorSystem.Application.Tests;

/// <summary>Clearing the queue through the controller (ADR-0032): in order with the requests, then off to a floor.</summary>
public sealed class ElevatorControllerClearTests
{
    [Fact]
    public async Task ClearRequests_CancelsEvenRequestsNotYetTakenIn_ThenSendsTheCarToTheLobby()
    {
        await using var harness = ControllerHarness.Create(startFloor: 4).Start();
        await harness.SelectFloorAsync(8);

        // Submitted, but not awaited: the clear must still find it, because it joins the same queue.
        harness.Controller.RequestElevator(9, Direction.Down);
        harness.Controller.ClearRequests([], new Floor(1)).ShouldBeTrue();
        var cleared = await harness.NextAsync<RequestsCleared>();

        cleared.CancelledCalls.Select(call => call.Call).ShouldBe([new CarCall(new Floor(8)), new HallCall(new Floor(9), Direction.Down)]);
        await harness.AdvanceUntilAsync<DoorsOpened>(opened => opened.Floor == new Floor(1));
        harness.Probe.Events.OfType<DoorsOpened>().ShouldNotContain(opened => opened.Floor == new Floor(8) || opened.Floor == new Floor(9));
    }

    [Fact]
    public async Task ClearRequests_KeepsTheGivenCalls_InTheirPlace()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1).Start();
        await harness.CallElevatorAsync(6, Direction.Down);
        await harness.SelectFloorAsync(9);

        harness.Controller.ClearRequests([new HallCall(new Floor(6), Direction.Down)]);
        await harness.NextAsync<RequestsCleared>();

        harness.Elevator.Snapshot().PendingCalls.Select(call => call.Call).ShouldBe([new HallCall(new Floor(6), Direction.Down)]);
    }

    [Fact]
    public async Task ClearRequests_OutOfService_DoesNotSendTheCarAnywhere()
    {
        await using var harness = ControllerHarness.Create(startFloor: 5).Start();
        harness.Controller.StartMaintenance();

        harness.Controller.ClearRequests([], new Floor(1)).ShouldBeTrue();
        await harness.StepsAsync(2);

        harness.Elevator.Snapshot().PendingCalls.ShouldBeEmpty();
        harness.Probe.Events.OfType<RequestRejected>().ShouldBeEmpty();
    }

    [Fact]
    public async Task ClearRequests_ToAFloorOutsideTheBuilding_IsRefused()
    {
        await using var harness = ControllerHarness.Create();

        Should.Throw<ArgumentOutOfRangeException>(() => harness.Controller.ClearRequests([], new Floor(42)));
    }
}
