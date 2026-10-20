using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;

namespace ElevatorSystem.Application.Tests;

/// <summary>Fast-forwarding a simulated car (ADR-0031): trips and door countdowns get shorter; the watchdog keeps its limit.</summary>
public sealed class ElevatorControllerSpeedTests
{
    private const double Speed = 4;
    private static readonly TimeSpan FastStep = ControllerHarness.Step / Speed;

    [Fact]
    public async Task SetSpeed_MakesTripsAndTheDoorCountdownThatMuchShorter()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1).Start();
        harness.Controller.SetSpeed(Speed).ShouldBeTrue();
        await harness.SelectFloorAsync(2);
        var departure = await harness.NextAsync<ElevatorMoved>();

        harness.Time.Advance(FastStep);
        var opened = await harness.NextAsync<DoorsOpened>();
        harness.Controller.DoorsCloseAt.ShouldBe(opened.OccurredAt + FastStep);
        harness.Time.Advance(FastStep);
        var closed = await harness.NextAsync<DoorsClosed>();

        (opened.OccurredAt - departure.OccurredAt).ShouldBe(FastStep);
        (closed.OccurredAt - opened.OccurredAt).ShouldBe(FastStep);
        harness.Controller.Speed.ShouldBe(Speed);
        harness.SimulatedDrive!.Speed.ShouldBe(Speed);
    }

    [Fact]
    public async Task SetSpeed_KeepsTheWatchdogLimit_SoASlowHostIsNotAFault_ButAJamIsStillCaught()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1).Start();
        harness.Controller.SetSpeed(Speed);
        harness.SimulatedDrive!.SimulateStuckCar();
        await harness.SelectFloorAsync(5);
        var departure = await harness.NextAsync<ElevatorMoved>();

        harness.Time.Advance(ControllerHarness.DefaultOptions.StuckTimeout - FastStep);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        harness.Elevator.State.ShouldBe(ElevatorState.MovingUp, "a trip many times longer than a fast one is still within the normal limit");
        harness.Time.Advance(FastStep);
        var outOfService = await harness.NextAsync<ElevatorTakenOutOfService>();

        outOfService.Reason.ShouldBe(OutOfServiceReason.Fault);
        (outOfService.OccurredAt - departure.OccurredAt).ShouldBe(ControllerHarness.DefaultOptions.StuckTimeout);
    }

    [Fact]
    public async Task SetSpeed_BackToOne_RestoresTheNormalPace()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1).Start();
        harness.Controller.SetSpeed(Speed);
        harness.Controller.SetSpeed(1);
        await harness.SelectFloorAsync(2);
        var departure = await harness.NextAsync<ElevatorMoved>();

        harness.Time.Advance(ControllerHarness.Step);
        var opened = await harness.NextAsync<DoorsOpened>();

        (opened.OccurredAt - departure.OccurredAt).ShouldBe(ControllerHarness.Step);
        harness.Controller.Speed.ShouldBe(1);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0)]
    [InlineData(ElevatorController.MaxSpeed + 1)]
    public async Task SetSpeed_OutOfRange_IsRefused(double speed)
    {
        await using var harness = ControllerHarness.Create();

        Should.Throw<ArgumentOutOfRangeException>(() => harness.Controller.SetSpeed(speed));
        harness.Controller.Speed.ShouldBe(1);
    }

    [Fact]
    public async Task SetSpeed_WithADriveThatCannotChangePace_KeepsTheNormalSpeed()
    {
        await using var harness = ControllerHarness.Create(drive: _ => new FailingDrive());

        harness.Controller.SetSpeed(Speed).ShouldBeFalse();
        harness.Controller.SetSpeed(1).ShouldBeTrue();
        harness.Controller.Speed.ShouldBe(1);
    }

    [Fact]
    public async Task SpeedChanged_IsRaisedOncePerChange()
    {
        await using var harness = ControllerHarness.Create();
        var changes = new List<double>();
        harness.Controller.SpeedChanged += (_, speed) => changes.Add(speed);

        harness.Controller.SetSpeed(Speed);
        harness.Controller.SetSpeed(Speed);
        harness.Controller.SetSpeed(1);

        changes.ShouldBe([Speed, 1]);
    }

    [Fact]
    public async Task SpeedChanged_AFailingHandler_DoesNotStopTheChange()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1).Start();
        harness.Controller.SpeedChanged += (_, _) => throw new InvalidOperationException("subscriber failure");

        harness.Controller.SetSpeed(Speed).ShouldBeTrue();
        await harness.SelectFloorAsync(2);
        await harness.NextAsync<ElevatorMoved>();
        harness.Time.Advance(FastStep);

        await harness.NextAsync<DoorsOpened>();
        harness.Controller.Speed.ShouldBe(Speed);
    }
}
