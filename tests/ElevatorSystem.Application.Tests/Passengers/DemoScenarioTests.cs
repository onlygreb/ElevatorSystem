using ElevatorSystem.Application.Passengers;
using ElevatorSystem.Application.Simulation;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace ElevatorSystem.Application.Tests.Passengers;

/// <summary>
/// Demo scenarios (ADR-0027) against a real control loop, in simulated time: simulated passengers arrive on floor 1,
/// walk in and out like people, and reach where their plan takes them.
/// </summary>
public sealed class DemoScenarioTests
{
    private static readonly ElevatorControllerOptions HoldOpen = ControllerHarness.DefaultOptions with { HoldDoorOpenWhenIdle = true };
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task MorningRush_TakesEveryoneUpToTheirFloor()
    {
        await using var world = World.Create(seed: 11);

        var added = world.Runner.Start(DemoScenario.MorningRush);
        await world.RunUntilAsync(() => world.Runner.Active == 0);

        added.ShouldBe(12);
        var counts = world.Registry.Current;
        counts.PassengersInCar.ShouldBe(0);
        counts.Floors[0].Present.ShouldBe(0, "everyone left the lobby");
        counts.Floors.Sum(floor => floor.Present).ShouldBe(12);
        counts.Floors.Sum(floor => floor.Waiting).ShouldBe(0);
        counts.PassengersDelivered.ShouldBe(12);
        world.Registry.Passengers.ShouldAllBe(passenger => passenger.Simulated);
    }

    [Fact]
    public async Task LunchBreak_BringsEveryoneBackDown_AndOutOfTheBuilding()
    {
        await using var world = World.Create(seed: 5);

        world.Runner.Start(DemoScenario.LunchBreak);
        await world.RunUntilAsync(() => world.Runner.Active == 0);

        world.Registry.Passengers.ShouldBeEmpty("they rode back to the lobby and went out");
        world.Registry.Current.PassengersDelivered.ShouldBe(16, "eight rides up and eight back down");
    }

    [Fact]
    public async Task ACrowdBiggerThanTheCar_TakesSeveralTrips_AndNobodyIsLeftBehind()
    {
        await using var world = World.Create(seed: 3, capacity: 3);

        world.Runner.Start(DemoScenario.MorningRush);
        await world.RunUntilAsync(() => world.Runner.Active == 0);

        world.Registry.Current.PassengersDelivered.ShouldBe(12);
        world.Harness.Probe.Events.OfType<Domain.Events.DoorsOpened>().Count(opened => opened.Floor == new Floor(1)).ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task StopAll_RemovesEverySimulatedPassenger_AndLeavesPeopleAlone()
    {
        await using var world = World.Create(seed: 1);
        var person = Guid.NewGuid();
        world.Registry.Join(person);
        world.Runner.Start(DemoScenario.BusyDay);
        await world.RunForAsync(TimeSpan.FromSeconds(20));

        var removed = world.Runner.StopAll();

        removed.ShouldBeGreaterThan(0);
        world.Runner.Active.ShouldBe(0);
        world.Registry.Passengers.ShouldHaveSingleItem().PassengerId.ShouldBe(person);
    }

    [Fact]
    public async Task HugeLoad_FastForwardsEverything_ServesEveryone_AndReturnsToNormalSpeed()
    {
        await using var world = World.Create(seed: 8, hugeLoad: 30);

        var added = world.Runner.Start(DemoScenario.HugeLoad);
        world.Runner.Speed.ShouldBe(4);
        world.Harness.Controller.Speed.ShouldBe(4);
        await world.RunUntilAsync(() => world.Runner.Active == 0);

        added.ShouldBe(30);
        world.Registry.Passengers.ShouldBeEmpty("everyone rode back to the lobby and went out");
        world.Registry.Current.PassengersDelivered.ShouldBeGreaterThanOrEqualTo(60, "each rode at least up and back down");
        world.Harness.Probe.Events.OfType<Domain.Events.CallMerged>().ShouldNotBeEmpty("impatient people pressed the button again");
        world.Runner.Speed.ShouldBe(1);
        world.Harness.Controller.Speed.ShouldBe(1);
    }

    [Fact]
    public async Task HugeLoad_FastForwardsTheOtherScenariosToo_UntilTheScenariosStopAndTheirCallsAreServed()
    {
        await using var world = World.Create(seed: 4, hugeLoad: 30);
        world.Runner.Start(DemoScenario.BusyDay);
        world.Runner.Speed.ShouldBe(1, "only a huge load fast-forwards");

        world.Runner.Start(DemoScenario.HugeLoad);
        await world.RunForAsync(TimeSpan.FromSeconds(10));
        world.Runner.Speed.ShouldBe(4);
        world.Harness.Elevator.Snapshot().PendingCalls.ShouldNotBeEmpty("the crowd is still calling");
        world.Runner.StopAll();
        world.Runner.Speed.ShouldBe(4, "the car first serves the calls the crowd left behind");
        await world.RunUntilAsync(() => world.Runner.Speed == 1);

        world.Harness.Elevator.Snapshot().PendingCalls.ShouldBeEmpty();
        world.Harness.Controller.Speed.ShouldBe(1);
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(120, 0.5)]
    [InlineData(120, ElevatorController.MaxSpeed + 1)]
    public void HugeLoadSettings_OutOfRange_AreRefused(int passengers, double speed) =>
        Should.Throw<ArgumentOutOfRangeException>(() => new SimulationOptions { HugeLoadPassengers = passengers, HugeLoadSpeed = speed }.Validate());

    [Fact]
    public async Task WhenDisabled_NothingStarts()
    {
        await using var world = World.Create(seed: 1, enabled: false);

        world.Runner.Start(DemoScenario.MorningRush).ShouldBe(0);
        world.Runner.Active.ShouldBe(0);
    }

    [Fact]
    public async Task TheSameSeed_GivesTheSameRun()
    {
        await using var first = World.Create(seed: 21);
        await using var second = World.Create(seed: 21);

        first.Runner.Start(DemoScenario.MorningRush);
        second.Runner.Start(DemoScenario.MorningRush);
        await first.RunUntilAsync(() => first.Runner.Active == 0);
        await second.RunUntilAsync(() => second.Runner.Active == 0);

        second.Registry.Current.Floors.ShouldBe(first.Registry.Current.Floors);
    }

    /// <summary>A controller in simulated time with passengers and a scenario runner on top of it.</summary>
    private sealed class World : IAsyncDisposable
    {
        private World(ControllerHarness harness, PassengerRegistry registry, DemoScenarioRunner runner)
        {
            Harness = harness;
            Registry = registry;
            Runner = runner;
        }

        public ControllerHarness Harness { get; }

        public PassengerRegistry Registry { get; }

        public DemoScenarioRunner Runner { get; }

        public static World Create(int seed, int capacity = 8, bool enabled = true, int hugeLoad = 200)
        {
            var harness = ControllerHarness.Create(startFloor: 1, options: HoldOpen).Start();
            var registry = new PassengerRegistry(
                harness.Elevator, new PassengerOptions { CarCapacity = capacity }, harness.Time, NullLogger<PassengerRegistry>.Instance);
            var runner = new DemoScenarioRunner(
                new PassengerService(registry, harness.Controller, harness.Time),
                harness.Elevator,
                new SimulationOptions { Enabled = enabled, Seed = seed, TickInterval = Tick, HugeLoadPassengers = hugeLoad, HugeLoadSpeed = 4 },
                harness.Time,
                NullLogger<DemoScenarioRunner>.Instance);
            return new World(harness, registry, runner);
        }

        public async Task RunUntilAsync(Func<bool> done, int maxTicks = 4000)
        {
            for (var tick = 0; tick < maxTicks && !done(); tick++)
            {
                await StepAsync();
            }

            done().ShouldBeTrue($"the scenario did not finish within {maxTicks} ticks; {Runner.Active} simulated passengers still acting");
        }

        public async Task RunForAsync(TimeSpan duration)
        {
            for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += Tick)
            {
                await StepAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            Runner.Dispose();
            await Harness.DisposeAsync();
        }

        private async Task StepAsync()
        {
            // The runner's timer fires inside Advance; the control loop reacts on the thread pool.
            Harness.Time.Advance(Tick);
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
    }
}
