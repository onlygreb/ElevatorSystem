using ElevatorSystem.Application.Simulation;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;

namespace ElevatorSystem.Application.Tests;

/// <summary>The synchronous answer to a request: accepted with an identifier, or refused with a reason.</summary>
public sealed class ElevatorControllerRequestTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(-5)]
    [InlineData(int.MinValue)]
    public async Task RequestElevator_FloorOutsideBuilding_IsRefusedAsInvalidFloor(int floor)
    {
        await using var harness = ControllerHarness.Create().Start();

        var result = harness.Controller.RequestElevator(floor, Direction.Up);

        result.IsAccepted.ShouldBeFalse();
        result.RequestId.ShouldBeNull();
        result.RejectionReason.ShouldBe(RequestRejectionReason.InvalidFloor);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public async Task RequestDestination_FloorOutsideBuilding_IsRefusedAsInvalidFloor(int floor)
    {
        await using var harness = ControllerHarness.Create().Start();

        harness.Controller.RequestDestination(floor).RejectionReason.ShouldBe(RequestRejectionReason.InvalidFloor);
    }

    [Theory]
    [InlineData(10, Direction.Up)]
    [InlineData(1, Direction.Down)]
    [InlineData(4, (Direction)9)]
    public async Task RequestElevator_ImpossibleDirection_IsRefusedAsInvalidDirection(int floor, Direction direction)
    {
        await using var harness = ControllerHarness.Create().Start();

        harness.Controller.RequestElevator(floor, direction).RejectionReason.ShouldBe(RequestRejectionReason.InvalidDirection);
    }

    [Fact]
    public async Task RequestElevator_ValidCall_IsAcceptedWithAnIdentifierAndQueued()
    {
        await using var harness = ControllerHarness.Create().Start();

        var result = await harness.CallElevatorAsync(6, Direction.Down);

        result.IsAccepted.ShouldBeTrue();
        result.RejectionReason.ShouldBeNull();
        harness.Probe.Events.OfType<Domain.Events.CallQueued>().ShouldHaveSingleItem().Request.Id.ShouldBe(result.RequestId!.Value);
    }

    [Fact]
    public async Task RequestElevator_WhileOutOfService_IsRefused()
    {
        await using var harness = ControllerHarness.Create().Start();
        harness.Controller.StartMaintenance();

        harness.Controller.RequestElevator(3, Direction.Up).RejectionReason.ShouldBe(RequestRejectionReason.OutOfService);
        harness.Controller.RequestDestination(3).RejectionReason.ShouldBe(RequestRejectionReason.OutOfService);
    }

    [Fact]
    public async Task RequestDestination_ToAFloorThatDoesNotExist_WhileOutOfService_IsRefusedForTheFloor()
    {
        await using var harness = ControllerHarness.Create().Start();
        harness.Controller.StartMaintenance();

        harness.Controller.RequestDestination(42).RejectionReason.ShouldBe(RequestRejectionReason.InvalidFloor, "the floor never exists; the outage passes");
    }

    [Fact]
    public void RequestElevator_IntakeFull_IsRefusedAsBusyInsteadOfBlocking()
    {
        var harness = ControllerHarness.Create(options: ControllerHarness.DefaultOptions with { IntakeCapacity = 2 });

        harness.Controller.RequestDestination(3).IsAccepted.ShouldBeTrue();
        harness.Controller.RequestDestination(4).IsAccepted.ShouldBeTrue();
        var third = harness.Controller.RequestDestination(5);

        third.RejectionReason.ShouldBe(RequestRejectionReason.Busy, "the control loop is not running, so the intake fills up");
    }

    [Fact]
    public void Constructor_InvalidOptions_Throws()
    {
        var time = TimeProvider.System;

        Should.Throw<ArgumentOutOfRangeException>(() => new ElevatorController(
            new Elevator(Building.Default, time),
            new FifoSchedulingStrategy(),
            new SimulatedElevatorDrive(TimeSpan.FromSeconds(1), time),
            new EventProbe(),
            new ElevatorControllerOptions { StuckTolerance = 1 },
            time,
            NullLogger<ElevatorController>.Instance));
    }
}
