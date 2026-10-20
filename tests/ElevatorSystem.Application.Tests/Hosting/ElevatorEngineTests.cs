using ElevatorSystem.Application.Hosting;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;

namespace ElevatorSystem.Application.Tests.Hosting;

/// <summary>The engine's cycle on the in-memory ports: leadership, commands, updates, restore and failover.</summary>
public sealed class ElevatorEngineTests
{
    [Fact]
    public async Task RunAsync_FirstStart_LeadsAndOpensTheDoorAtTheLowestFloor()
    {
        await using var harness = new EngineHarness();

        var engine = await harness.StartLeaderAsync();

        engine.Engine.Controller.ShouldNotBeNull();
        var opened = harness.Updates.Messages.First(update => update.Activity.Kind == ElevatorActivityKind.DoorsOpened);
        opened.Status.CurrentFloor.ShouldBe(1);
        opened.Status.Version.ShouldBeGreaterThan(0, "the start-up door cycle is a real transition with a fresh version");
    }

    [Fact]
    public async Task HallCall_IsQueuedWithTheCommandIdAsItsRequestId_AndServed()
    {
        await using var harness = new EngineHarness();
        await harness.StartLeaderAsync();
        var command = EngineHarness.HallCall(6, CallDirection.Down);

        await harness.SendAsync(command);

        var queued = await harness.Updates.WaitForAsync(update => update.Activity.Kind == ElevatorActivityKind.CallQueued);
        queued.Activity.RequestId.ShouldBe(command.CommandId, "the id the API returned is the id the passenger can follow");
        queued.Status.PendingCalls.ShouldContain(pending => pending.Call.Floor == 6 && pending.Call.Direction == CallDirection.Down);
        var served = await harness.Updates.WaitForAsync(update => update.Activity.Kind == ElevatorActivityKind.CallServed && update.Activity.Floor == 6);
        served.Activity.RequestId.ShouldBe(command.CommandId);
        served.Activity.WaitTime.ShouldNotBeNull().ShouldBeGreaterThan(TimeSpan.Zero, "the wait counts from when the passenger pressed the button");
    }

    [Fact]
    public async Task DuplicateCommand_IsAppliedOnce()
    {
        await using var harness = new EngineHarness();
        await harness.StartLeaderAsync();
        var command = EngineHarness.CarCall(7);

        await harness.SendAsync(command);
        await harness.SendAsync(command);
        await harness.SendAsync(EngineHarness.CarCall(2));

        await harness.Updates.WaitForAsync(update => update.Activity.Kind == ElevatorActivityKind.CallServed && update.Activity.Floor == 2);
        var forCommand = harness.Updates.Messages.Where(update => update.Activity.RequestId == command.CommandId).Select(update => update.Activity.Kind);
        forCommand.ShouldBe([ElevatorActivityKind.CallQueued, ElevatorActivityKind.CallServed], "the copy is dropped, not merged into the pending call");
        harness.Logs.Contains(2015).ShouldBeTrue("dropping a duplicate is logged");
    }

    [Fact]
    public async Task RefusedCall_IsReportedAsRequestRejected()
    {
        await using var harness = new EngineHarness();
        await harness.StartLeaderAsync();
        var command = EngineHarness.CarCall(99);

        await harness.SendAsync(command);

        var rejected = await harness.Updates.WaitForAsync(update => update.Activity.Kind == ElevatorActivityKind.RequestRejected);
        rejected.Activity.RequestId.ShouldBe(command.CommandId);
        rejected.Activity.RejectionReason.ShouldBe(RejectionReason.InvalidFloor);
    }

    [Fact]
    public async Task ServiceCommands_TakeTheCarOutOfServiceAndBack()
    {
        await using var harness = new EngineHarness();
        await harness.StartLeaderAsync();

        await harness.SendAsync(EngineHarness.Service(ServiceAction.EmergencyStop));
        var stopped = await harness.Updates.WaitForAsync(update => update.Activity.Kind == ElevatorActivityKind.OutOfService);
        await harness.SendAsync(EngineHarness.CarCall(5));
        var refused = await harness.Updates.WaitForAsync(update => update.Activity.Kind == ElevatorActivityKind.RequestRejected);
        await harness.SendAsync(EngineHarness.Service(ServiceAction.ReturnToService));
        var returned = await harness.Updates.WaitForAsync(update => update.Activity.Kind == ElevatorActivityKind.ReturnedToService);

        stopped.Activity.OutOfServiceCause.ShouldBe(OutOfServiceCause.EmergencyStop);
        stopped.Status.Status.ShouldBe(ElevatorStatus.OutOfService);
        refused.Activity.RejectionReason.ShouldBe(RejectionReason.OutOfService);
        returned.Status.Status.ShouldBe(ElevatorStatus.Idle);
    }

    [Fact]
    public async Task ServiceCommand_ThatDoesNotApply_IsLoggedAndTheEngineCarriesOn()
    {
        await using var harness = new EngineHarness();
        await harness.StartLeaderAsync();

        await harness.SendAsync(EngineHarness.Service(ServiceAction.ReturnToService));
        await harness.SendAsync(EngineHarness.CarCall(3));

        await harness.Updates.WaitForAsync(update => update.Activity.Kind == ElevatorActivityKind.CallServed && update.Activity.Floor == 3);
        harness.Logs.Contains(2017).ShouldBeTrue("returning a car that is in service is refused and logged");
    }

    [Fact]
    public async Task CommandsSentWhileNobodyLeads_AreServedOnceAnEngineLeads()
    {
        await using var harness = new EngineHarness();
        var command = EngineHarness.CarCall(4);
        await harness.SendAsync(command);

        harness.Start();

        var served = await harness.Updates.WaitForAsync(update => update.Activity.Kind == ElevatorActivityKind.CallServed && update.Activity.Floor == 4);
        served.Activity.RequestId.ShouldBe(command.CommandId);
    }

    [Fact]
    public async Task Restart_RestoresTheFloorAndThePendingCalls_WithAHigherVersion()
    {
        await using var harness = new EngineHarness
        {
            // Slow travel, so calls are still pending when the engine stops.
            ControllerOptions = new ElevatorControllerOptions { TravelTimePerFloor = TimeSpan.FromSeconds(30), DoorOpenDuration = EngineHarness.DoorTime, StuckTolerance = 100 },
        };
        var first = await harness.StartLeaderAsync();
        await harness.SendAsync(EngineHarness.CarCall(5));
        await harness.SendAsync(EngineHarness.HallCall(8, CallDirection.Down));
        var beforeStop = await harness.Updates.WaitForAsync(update => update.Status.PendingCalls.Count == 2);

        await first.StopAsync();
        var stored = await harness.StateStore.LoadAsync(TestContext.Current.CancellationToken);
        var lastVersion = harness.Updates.Messages[^1].Status.Version;
        var from = harness.Updates.Count;
        await harness.StartLeaderAsync();

        stored.ShouldNotBeNull();
        stored.CurrentFloor.Number.ShouldBe(beforeStop.Status.CurrentFloor, "the car stopped at the floor it was heading to");
        var restored = harness.Updates.Messages[from];
        restored.Activity.Kind.ShouldBe(ElevatorActivityKind.DoorsOpened);
        restored.Status.CurrentFloor.ShouldBe(stored.CurrentFloor.Number);
        restored.Status.PendingCalls.Select(pending => pending.Call.Floor).ShouldBe([5, 8]);
        restored.Status.Version.ShouldBeGreaterThan(lastVersion, "a browser must accept the restored state without reconnecting");
    }

    [Fact]
    public async Task GracefulStop_StopsTheCarWithItsDoorOpen_AndSavesThatState()
    {
        await using var harness = new EngineHarness
        {
            ControllerOptions = new ElevatorControllerOptions { TravelTimePerFloor = TimeSpan.FromSeconds(30), DoorOpenDuration = EngineHarness.DoorTime, StuckTolerance = 100 },
        };
        var engine = await harness.StartLeaderAsync();
        await harness.SendAsync(EngineHarness.CarCall(9));
        await harness.Updates.WaitForAsync(update => update.Status.Status == ElevatorStatus.MovingUp);

        await engine.StopAsync();

        var stored = await harness.StateStore.LoadAsync(TestContext.Current.CancellationToken);
        stored.ShouldNotBeNull().State.ShouldBe(ElevatorState.DoorOpen);
        harness.Updates.Messages[^1].Status.Status.ShouldBe(ElevatorStatus.DoorOpen, "subscribers see the car parked before the engine goes");
        engine.Engine.Role.ShouldBe(EngineRole.Stopped);
        harness.Elector.IsHeld.ShouldBeFalse("leadership is released on the way out");
    }

    [Fact]
    public async Task LeadershipLost_StopsDrivingAtOnce_AndTheStandbyTakesOverFromTheSnapshot()
    {
        await using var harness = new EngineHarness
        {
            ControllerOptions = new ElevatorControllerOptions { TravelTimePerFloor = TimeSpan.FromMilliseconds(200), DoorOpenDuration = EngineHarness.DoorTime, StuckTolerance = 100 },
        };
        var first = await harness.StartLeaderAsync();
        var second = harness.Start();
        await second.WaitForRoleAsync(EngineRole.Standby);
        var call = EngineHarness.CarCall(10);
        await harness.SendAsync(call);
        await harness.Updates.WaitForAsync(update => update.Status.Status == ElevatorStatus.MovingUp);
        var firstController = first.Engine.Controller.ShouldNotBeNull();

        harness.Elector.RevokeLeadership();

        await second.WaitForRoleAsync(EngineRole.Leader);
        await first.WaitForRoleAsync(EngineRole.Standby);
        first.Engine.Controller.ShouldBeNull();
        var versionWhenDeposed = firstController.Elevator.Snapshot().Version;
        var served = await harness.Updates.WaitForAsync(update => update.Activity.Kind == ElevatorActivityKind.CallServed && update.Activity.Floor == 10);
        served.Activity.RequestId.ShouldBe(call.CommandId, "the accepted call survives the failover");
        firstController.Elevator.Snapshot().Version.ShouldBe(versionWhenDeposed, "the deposed engine never moves the car again");
    }

    [Fact]
    public async Task RestoredOutOfServiceCar_StaysOutOfService_WithItsDoorClosed()
    {
        await using var harness = new EngineHarness();
        var first = await harness.StartLeaderAsync();
        await harness.SendAsync(EngineHarness.Service(ServiceAction.StartMaintenance));
        await harness.Updates.WaitForAsync(update => update.Activity.Kind == ElevatorActivityKind.OutOfService);
        await first.StopAsync();
        var from = harness.Updates.Count;

        var second = harness.Start();
        await second.WaitForRoleAsync(EngineRole.Leader);
        await harness.SendAsync(EngineHarness.Service(ServiceAction.ReturnToService));

        var returned = await harness.Updates.WaitForAsync(update => update.Activity.Kind == ElevatorActivityKind.ReturnedToService, from);
        harness.Updates.Messages.Skip(from).TakeWhile(update => update != returned)
            .ShouldNotContain(update => update.Activity.Kind == ElevatorActivityKind.DoorsOpened, "maintenance survives a restart until it is cleared");
    }

    [Fact]
    public async Task Occupancy_IsPublishedOnTakeOver_AndOnEveryChange()
    {
        await using var harness = new EngineHarness();
        await harness.StartLeaderAsync();
        var initial = await harness.OccupancyUpdates.WaitForAsync(_ => true);

        await harness.SendAsync(EngineHarness.HallCall(6, CallDirection.Down));
        var waiting = await harness.OccupancyUpdates.WaitForAsync(update => update.Occupancy.Floors[5].Waiting == 1);
        var riding = await harness.OccupancyUpdates.WaitForAsync(update => update.Occupancy.PassengersInCar == 1);
        await harness.SendAsync(EngineHarness.CarCall(2));
        var delivered = await harness.OccupancyUpdates.WaitForAsync(update => update.Occupancy.PassengersDelivered == 1);

        initial.Occupancy.PassengersInCar.ShouldBe(0, "a new term counts from nothing; restored calls are counted when served");
        initial.Occupancy.Floors.Count.ShouldBe(10);
        riding.Occupancy.Floors[5].Waiting.ShouldBe(0, "the person boarded when the door opened at 6");
        delivered.Occupancy.Floors[1].Present.ShouldBe(1);
        delivered.Occupancy.PassengersInCar.ShouldBe(0);
        harness.OccupancyUpdates.Messages.Select(update => update.UpdateId).ShouldBeUnique();
        waiting.Occupancy.TakenAt.ShouldBeLessThanOrEqualTo(delivered.Occupancy.TakenAt);
    }

    [Fact]
    public async Task RunAsync_Twice_IsRefused()
    {
        await using var harness = new EngineHarness();
        var running = await harness.StartLeaderAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => running.Engine.RunAsync(TestContext.Current.CancellationToken));
    }
}
