using ElevatorSystem.Application.Diagnostics;
using ElevatorSystem.Application.Hosting;
using ElevatorSystem.Application.Simulation;
using ElevatorSystem.Application.Tests.Diagnostics;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

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
        // Travelling: with both calls in, the car may still be at floor 1 with its doors open a moment before it leaves.
        var beforeStop = await harness.Updates.WaitForAsync(update => update.Status is { PendingCalls.Count: 2, Status: ElevatorStatus.MovingUp });

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
    public async Task Occupancy_IsPublishedOnTakeOver_AndOnEveryPassengerStep()
    {
        await using var harness = new EngineHarness
        {
            // The default door policy: the car waits open at floor 1, so the passenger can walk in.
            ControllerOptions = new ElevatorControllerOptions { TravelTimePerFloor = EngineHarness.TravelTime, DoorOpenDuration = TimeSpan.FromSeconds(1), StuckTolerance = 100 },
        };
        await harness.StartLeaderAsync();
        var initial = await harness.OccupancyUpdates.WaitForAsync(_ => true);
        var passenger = Guid.NewGuid();

        await harness.SendAsync(EngineHarness.Passenger(passenger, PassengerAction.Join));
        var present = await harness.OccupancyUpdates.WaitForAsync(update => update.Occupancy.Floors[0].Present == 1);
        await harness.SendAsync(EngineHarness.Passenger(passenger, PassengerAction.Board));
        var riding = await harness.OccupancyUpdates.WaitForAsync(update => update.Occupancy.PassengersInCar == 1);
        await harness.SendAsync(EngineHarness.Passenger(passenger, PassengerAction.ChooseFloor, floor: 3));
        await harness.Updates.WaitForAsync(update => update.Activity is { Kind: ElevatorActivityKind.DoorsOpened, Floor: 3 });
        await harness.SendAsync(EngineHarness.Passenger(passenger, PassengerAction.Leave));
        var delivered = await harness.OccupancyUpdates.WaitForAsync(update => update.Occupancy.PassengersDelivered == 1);

        initial.Occupancy.PassengersInCar.ShouldBe(0, "a new term starts with nobody in the building");
        initial.Occupancy.Floors.Count.ShouldBe(10);
        present.Occupancy.PassengersInCar.ShouldBe(0);
        riding.Occupancy.Floors[0].Present.ShouldBe(0, "they walked from the lobby into the car");
        delivered.Occupancy.Floors[2].Present.ShouldBe(1);
        delivered.Occupancy.PassengersInCar.ShouldBe(0);
        harness.OccupancyUpdates.Messages.Select(update => update.UpdateId).ShouldBeUnique();
        harness.Updates.Messages.Select(update => update.Activity.Kind).ShouldContain(ElevatorActivityKind.PassengerLeft);
    }

    [Fact]
    public async Task Metrics_FollowTheCarAndThePassengers_WhileTheEngineLeads()
    {
        using var meters = new TestMeterFactory();
        using var trips = new MetricCollector<long>(meters, ElevatorMetrics.MeterName, "elevator.floors_travelled");
        using var steps = new MetricCollector<long>(meters, ElevatorMetrics.MeterName, "elevator.passenger.steps");
        using var floor = new MetricCollector<int>(meters, ElevatorMetrics.MeterName, "elevator.floor");
        await using var harness = new EngineHarness { Metrics = new ElevatorMetrics(meters) };
        var engine = await harness.StartLeaderAsync();
        var passenger = Guid.NewGuid();

        await harness.SendAsync(EngineHarness.Passenger(passenger, PassengerAction.Join));
        await harness.SendAsync(EngineHarness.CarCall(4));
        await harness.Updates.WaitForAsync(update => update.Activity is { Kind: ElevatorActivityKind.CallServed, Floor: 4 });
        await harness.Updates.WaitForAsync(update => update.Activity.PassengerId == passenger);

        trips.GetMeasurementSnapshot().Count.ShouldBe(3, "floor 1 to 4");
        steps.GetMeasurementSnapshot().ShouldContain(m => (string?)m.Tags["elevator.passenger.step"] == "join");
        floor.RecordObservableInstruments();
        floor.LastMeasurement.ShouldNotBeNull().Value.ShouldBe(4);

        await engine.StopAsync();
        floor.Clear();
        floor.RecordObservableInstruments();
        floor.GetMeasurementSnapshot().ShouldBeEmpty("an engine that no longer leads reports no floor");
    }

    [Fact]
    public async Task PassengerSteps_ThatTheCarCannotAllow_AreReportedAsRefused()
    {
        await using var harness = new EngineHarness();
        await harness.StartLeaderAsync();
        var stranger = Guid.NewGuid();
        var passenger = Guid.NewGuid();

        await harness.SendAsync(EngineHarness.Passenger(stranger, PassengerAction.Board));
        await harness.SendAsync(EngineHarness.Passenger(passenger, PassengerAction.Join));
        await harness.SendAsync(EngineHarness.Passenger(passenger, PassengerAction.Leave));

        var unknown = await harness.Updates.WaitForAsync(update => update.Activity.PassengerId == stranger);
        var notInCar = await harness.Updates.WaitForAsync(update => update.Activity is { Kind: ElevatorActivityKind.PassengerRefused } && update.Activity.PassengerId == passenger);
        unknown.Activity.PassengerRefusal.ShouldBe(PassengerRefusalReason.UnknownPassenger);
        notInCar.Activity.PassengerRefusal.ShouldBe(PassengerRefusalReason.NotInCar);
        notInCar.Activity.PassengerAction.ShouldBe(PassengerAction.Leave);
    }

    [Fact]
    public async Task ScenarioCommands_StartSimulatedPassengers_AndStopRemovesThem()
    {
        await using var harness = new EngineHarness { Simulation = new SimulationOptions { Enabled = true, Seed = 7 } };
        var leader = await harness.StartLeaderAsync();

        await harness.SendAsync(new ScenarioCommandV1(Guid.CreateVersion7(), DateTimeOffset.UnixEpoch, DemoScenario.MorningRush));
        await harness.Updates.WaitForAsync(update => update.Activity is { Kind: ElevatorActivityKind.PassengerJoined, Simulated: true });
        await harness.SendAsync(new ScenarioCommandV1(Guid.CreateVersion7(), DateTimeOffset.UnixEpoch, null));
        await harness.Updates.WaitForAsync(update => update.Activity is { Kind: ElevatorActivityKind.PassengerRemoved, Simulated: true });

        for (var attempt = 0; attempt < 100 && leader.Engine.Passengers?.Passengers.Count > 0; attempt++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        leader.Engine.Passengers!.Passengers.ShouldBeEmpty("every simulated passenger left");
        leader.Engine.Scenarios!.Active.ShouldBe(0);
    }

    [Fact]
    public async Task HugeLoad_PublishesTheSpeedChange_AndEveryStatusCarriesIt()
    {
        await using var harness = new EngineHarness { Simulation = new SimulationOptions { Enabled = true, Seed = 7, HugeLoadPassengers = 5, HugeLoadSpeed = 4 } };
        await harness.StartLeaderAsync();

        await harness.SendAsync(new ScenarioCommandV1(Guid.CreateVersion7(), DateTimeOffset.UnixEpoch, DemoScenario.HugeLoad));
        var faster = await harness.Updates.WaitForAsync(update => update.Activity is { Kind: ElevatorActivityKind.SpeedChanged, Speed: 4 });
        var later = await harness.Updates.WaitForAsync(update => update.Activity is { Kind: ElevatorActivityKind.PassengerJoined, Simulated: true });
        await harness.SendAsync(new ScenarioCommandV1(Guid.CreateVersion7(), DateTimeOffset.UnixEpoch, null));
        var normal = await harness.Updates.WaitForAsync(update => update.Activity is { Kind: ElevatorActivityKind.SpeedChanged, Speed: 1 });

        faster.Status.Speed.ShouldBe(4);
        later.Status.Speed.ShouldBe(4);
        normal.Status.Speed.ShouldBe(1);
    }

    [Fact]
    public async Task ManyPressesOfOneCall_AreAllCounted_ButOnlyAFewMergesAreForwarded()
    {
        await using var harness = new EngineHarness
        {
            ControllerOptions = new ElevatorControllerOptions { TravelTimePerFloor = TimeSpan.FromMilliseconds(100), DoorOpenDuration = EngineHarness.DoorTime },
        };
        await harness.StartLeaderAsync();

        for (var press = 0; press < 20; press++)
        {
            await harness.SendAsync(EngineHarness.HallCall(10, CallDirection.Down));
        }

        var served = await harness.Updates.WaitForAsync(update => update.Activity is { Kind: ElevatorActivityKind.CallServed, Floor: 10 });
        served.Activity.RequestCount.ShouldBe(20, "the controller counts every press");
        harness.Updates.Messages.Count(update => update.Activity.Kind == ElevatorActivityKind.CallMerged)
            .ShouldBeLessThan(19, "merges are forwarded at most four times a second, so they cannot crowd out the car's steps");
    }

    [Fact]
    public async Task RunAsync_Twice_IsRefused()
    {
        await using var harness = new EngineHarness();
        var running = await harness.StartLeaderAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => running.Engine.RunAsync(TestContext.Current.CancellationToken));
    }
}
