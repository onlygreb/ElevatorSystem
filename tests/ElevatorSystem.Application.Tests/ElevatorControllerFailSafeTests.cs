using ElevatorSystem.Application.Simulation;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;

namespace ElevatorSystem.Application.Tests;

/// <summary>Watchdog, drive failures, safety commands and the loop's own resilience (ADR-0007).</summary>
public sealed class ElevatorControllerFailSafeTests
{
    private static readonly TimeSpan StuckTimeout = ControllerHarness.DefaultOptions.StuckTimeout;

    [Fact]
    public async Task ProcessRequests_TripThatNeverArrives_TakesTheElevatorOutOfServiceAfterTheTimeout()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1).Start();
        harness.SimulatedDrive!.SimulateStuckCar();
        await harness.SelectFloorAsync(5);
        var departure = await harness.NextAsync<ElevatorMoved>();

        harness.Time.Advance(StuckTimeout);
        var outOfService = await harness.NextAsync<ElevatorTakenOutOfService>();

        outOfService.Reason.ShouldBe(OutOfServiceReason.Fault);
        (outOfService.OccurredAt - departure.OccurredAt).ShouldBe(StuckTimeout);
        outOfService.CancelledCalls.ShouldHaveSingleItem().Call.ShouldBe(new CarCall(new Floor(5)));
        harness.Controller.RequestDestination(3).RejectionReason.ShouldBe(RequestRejectionReason.OutOfService);
    }

    [Fact]
    public async Task ProcessRequests_AnUnexpectedFailureInTheLoop_TakesTheCarOutOfService_AndTheLoopKeepsRunning()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1, strategy: new BrokenStrategy()).Start();

        harness.Controller.RequestDestination(5);
        var outOfService = await harness.NextAsync<ElevatorTakenOutOfService>();

        outOfService.Reason.ShouldBe(OutOfServiceReason.Fault);
        outOfService.CancelledCalls.ShouldHaveSingleItem().Call.ShouldBe(new CarCall(new Floor(5)));
        harness.Loop.IsCompleted.ShouldBeFalse("the loop fails safe instead of crashing");
        harness.Controller.ReturnToService().Succeeded.ShouldBeTrue();
        harness.Controller.RequestDestination(3).IsAccepted.ShouldBeTrue();
    }

    [Fact]
    public async Task ProcessRequests_SlowTripWithinTolerance_IsNotAFault()
    {
        var slowTravel = ControllerHarness.Step * 2;
        await using var harness = ControllerHarness.Create(
            startFloor: 1,
            drive: time => new SimulatedElevatorDrive(slowTravel, time)).Start();
        await harness.SelectFloorAsync(2);
        await harness.NextAsync<ElevatorMoved>();

        harness.Time.Advance(slowTravel);
        await harness.NextAsync<DoorsOpened>();

        harness.Probe.Events.OfType<ElevatorTakenOutOfService>().ShouldBeEmpty();
        slowTravel.ShouldBeLessThan(StuckTimeout);
    }

    [Fact]
    public async Task ProcessRequests_DriveReportsAFailure_TakesTheElevatorOutOfService()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1, drive: _ => new FailingDrive()).Start();

        await harness.SelectFloorAsync(5);
        var outOfService = await harness.NextAsync<ElevatorTakenOutOfService>();

        outOfService.Reason.ShouldBe(OutOfServiceReason.Fault);
    }

    [Fact]
    public async Task ProcessRequests_DriveThrowsBeforeStarting_TakesTheElevatorOutOfService()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1, drive: _ => new ThrowingDrive()).Start();

        await harness.SelectFloorAsync(5);
        var outOfService = await harness.NextAsync<ElevatorTakenOutOfService>();

        outOfService.Reason.ShouldBe(OutOfServiceReason.Fault);
        harness.Loop.IsCompleted.ShouldBeFalse("the loop survives the failure");
    }

    [Fact]
    public async Task EmergencyStop_DuringATrip_StopsAtOnceAndCancelsEveryCall()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1).Start();
        await harness.SelectFloorAsync(8);
        await harness.NextAsync<ElevatorMoved>();
        await harness.CallElevatorAsync(5, Direction.Down);

        var result = harness.Controller.EmergencyStop();

        result.Succeeded.ShouldBeTrue();
        var stopped = result.Events.ShouldHaveSingleItem().ShouldBeOfType<ElevatorTakenOutOfService>();
        stopped.Reason.ShouldBe(OutOfServiceReason.EmergencyStop);
        stopped.CancelledCalls.Select(call => call.Call)
            .ShouldBe([new CarCall(new Floor(8)), new HallCall(new Floor(5), Direction.Down)]);
        var snapshot = harness.Elevator.Snapshot();
        snapshot.State.ShouldBe(ElevatorState.OutOfService);
        snapshot.PendingCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task EmergencyStop_AbandonsTheTripInFlight()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1).Start();
        await harness.SelectFloorAsync(8);
        await harness.NextAsync<ElevatorMoved>();

        harness.Controller.EmergencyStop();
        harness.Time.Advance(ControllerHarness.Step * 10);
        var outOfService = await harness.NextAsync<ElevatorTakenOutOfService>();

        harness.Probe.Events.SkipWhile(e => e != outOfService).OfType<ElevatorMoved>().ShouldBeEmpty();
        harness.Elevator.CurrentFloor.ShouldBe(new Floor(2));
    }

    [Fact]
    public async Task ReturnToService_AfterEmergencyStop_ServesRequestsAgain()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1).Start();
        harness.Controller.EmergencyStop();

        var result = harness.Controller.ReturnToService();
        await harness.SelectFloorAsync(3);
        var events = await harness.RunUntilIdleAsync();

        result.Events.ShouldHaveSingleItem().ShouldBeOfType<ElevatorReturnedToService>();
        events.OfType<DoorsOpened>().ShouldHaveSingleItem().Floor.ShouldBe(new Floor(3));
    }

    [Fact]
    public async Task StartMaintenance_TakesTheElevatorOutOfServiceForMaintenance()
    {
        await using var harness = ControllerHarness.Create().Start();

        harness.Controller.StartMaintenance();

        harness.Elevator.Snapshot().OutOfServiceReason.ShouldBe(OutOfServiceReason.Maintenance);
    }

    [Fact]
    public async Task ProcessRequests_SubscriberThatThrows_DoesNotStopTheElevator()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1, sink: probe => new ThrowingSink(probe)).Start();

        await harness.SelectFloorAsync(3);
        var events = await harness.RunUntilIdleAsync();

        events.OfType<DoorsOpened>().ShouldHaveSingleItem().Floor.ShouldBe(new Floor(3));
    }

    [Fact]
    public async Task ProcessRequests_AlreadyRunning_Throws()
    {
        await using var harness = ControllerHarness.Create().Start();

        await Should.ThrowAsync<InvalidOperationException>(() => harness.Controller.ProcessRequests(CancellationToken.None));
    }

    [Fact]
    public async Task ProcessRequests_WhenCancelled_StopsCleanly()
    {
        var harness = ControllerHarness.Create(startFloor: 1).Start();
        await harness.SelectFloorAsync(8);
        await harness.NextAsync<ElevatorMoved>();

        await harness.DisposeAsync();

        harness.Loop.IsCompletedSuccessfully.ShouldBeTrue();
    }
}
