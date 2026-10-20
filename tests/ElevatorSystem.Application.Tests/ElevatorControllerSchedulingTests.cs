using ElevatorSystem.Domain.Events;
using ElevatorSystem.Domain.Scheduling;

namespace ElevatorSystem.Application.Tests;

/// <summary>Switching between FIFO and LOOK (ADR-0033): only while the car is out of service.</summary>
public sealed class ElevatorControllerSchedulingTests
{
    [Fact]
    public async Task ChangeScheduling_InService_IsRefused()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1).Start();

        harness.Controller.ChangeScheduling(SchedulingPolicy.Look).ShouldBeFalse();

        harness.Controller.Scheduling.ShouldBe(SchedulingPolicy.Fifo);
    }

    [Fact]
    public async Task ChangeScheduling_OutOfService_SwitchesTheStrategy_AndTheCarUsesItBackInService()
    {
        await using var harness = ControllerHarness.Create(startFloor: 5).Start();
        var changes = new List<SchedulingPolicy>();
        harness.Controller.SchedulingChanged += (_, policy) => changes.Add(policy);
        harness.Controller.EmergencyStop();

        harness.Controller.ChangeScheduling(SchedulingPolicy.Look).ShouldBeTrue();
        harness.Controller.ReturnToService();
        await harness.SelectFloorAsync(2);
        await harness.SelectFloorAsync(8);
        await harness.SelectFloorAsync(3);

        harness.Controller.Scheduling.ShouldBe(SchedulingPolicy.Look);
        harness.Controller.Strategy.ShouldBeOfType<LookSchedulingStrategy>();
        changes.ShouldBe([SchedulingPolicy.Look]);
        var stops = new List<int>();
        for (var stop = 0; stop < 3; stop++)
        {
            stops.Add((await harness.AdvanceUntilAsync<DoorsOpened>()).Floor.Number);
        }

        stops.ShouldBe([3, 2, 8], "LOOK keeps going down past 3 to 2 before turning up; FIFO would go to 2, then 8, then 3");
    }

    [Fact]
    public async Task ChangeScheduling_ToTheStrategyInUse_ChangesNothing()
    {
        await using var harness = ControllerHarness.Create().Start();
        var changes = 0;
        harness.Controller.SchedulingChanged += (_, _) => changes++;
        harness.Controller.StartMaintenance();

        harness.Controller.ChangeScheduling(SchedulingPolicy.Fifo).ShouldBeTrue();

        changes.ShouldBe(0);
    }

    [Fact]
    public async Task ChangeScheduling_UnknownPolicy_Throws()
    {
        await using var harness = ControllerHarness.Create();

        Should.Throw<ArgumentOutOfRangeException>(() => harness.Controller.ChangeScheduling((SchedulingPolicy)9));
    }
}
