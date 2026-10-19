using ElevatorSystem.Application.Simulation;
using ElevatorSystem.Application.Tests.Occupancy;
using Microsoft.Extensions.Logging;

namespace ElevatorSystem.Application.Tests.Simulation;

/// <summary>
/// The generator driving the real controller in simulated time, with the tracker counting. Its people
/// must be exactly where the tracker says, after every single event, through full cars, people left
/// behind and an outage.
/// </summary>
[Collection(CpuHeavyTests.Name)]
public sealed class TrafficGeneratorOccupancyTests
{
    private static readonly TrafficGeneratorOptions BusyMorning = new()
    {
        Enabled = true,
        ArrivalsPerMinute = 20,
        MaxActivePeople = 25,
        MaxPeople = 80,
        Seed = 7,
    };

    [Theory]
    [InlineData(7, 3)]
    [InlineData(8, 8)]
    [InlineData(9, 1)]
    public async Task Run_AgainstTheController_KeepsTheTrackerInStepWithThePeople(int seed, int capacity)
    {
        await using var simulation = TrafficSimulation.Create(BusyMorning with { Seed = seed }, capacity);
        simulation.Generator.Start(simulation.Harness.Controller).ShouldBeTrue();

        await simulation.RunUntilDoneAsync();

        simulation.Check.FirstMismatch.ShouldBeNull();
        simulation.Check.EventsChecked.ShouldBeGreaterThan(500);
        simulation.Check.MostRiding.ShouldBe(capacity, "the car filled up at least once");
        var people = simulation.Generator.Statistics;
        people.Arrivals.ShouldBe(80);
        (people.Delivered + people.Abandoned).ShouldBe(80, "everyone who appeared reached their floor");
        var totals = simulation.Tracker.Totals;
        totals.Riding.ShouldBe(0);
        (totals.Waiting + totals.LeftBehind).ShouldBe(0);
        totals.Delivered.ShouldBe(people.Delivered);
        totals.Boarded.ShouldBe(people.Delivered);
        totals.Corrections.ShouldBe(0);
        simulation.Tracker.Current.AverageWaitTime.ShouldNotBeNull().ShouldBeGreaterThan(TimeSpan.Zero);
        simulation.TrackerLog.Entries.ShouldNotContain(entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Run_WithASmallCar_LeavesPeopleBehindWhoCallAgainAndGetThere()
    {
        await using var simulation = TrafficSimulation.Create(BusyMorning, capacity: 2);
        simulation.Generator.Start(simulation.Harness.Controller);

        await simulation.RunUntilDoneAsync();

        simulation.Check.FirstMismatch.ShouldBeNull();
        simulation.Check.MostLeftBehind.ShouldBeGreaterThan(0, "a busy floor with a two-person car leaves people behind");
        simulation.Generator.Statistics.Delivered.ShouldBe(80 - simulation.Generator.Statistics.Abandoned);
        simulation.Tracker.Totals.PeopleCounted.ShouldBeLessThanOrEqualTo(80, "calling again never counts a new person");
    }

    [Theory]
    [InlineData(7, 3)]
    [InlineData(9, 1)]
    [InlineData(11, 2)]
    public async Task Run_WithAcknowledgementsArrivingSwapped_StaysInStep(int seed, int capacity)
    {
        await using var simulation = TrafficSimulation.Create(BusyMorning with { Seed = seed }, capacity, swapAcknowledgements: true);
        simulation.Generator.Start(simulation.Harness.Controller);

        await simulation.RunUntilDoneAsync();

        simulation.Check.FirstMismatch.ShouldBeNull("the counts must not depend on which of two racing presses is taken in first");
        simulation.Check.MostLeftBehind.ShouldBeGreaterThan(0);
        (simulation.Generator.Statistics.Delivered + simulation.Generator.Statistics.Abandoned).ShouldBe(80);
    }

    [Fact]
    public async Task Run_WithAnOutageInTheMiddle_StaysInStepAndFinishes()
    {
        await using var simulation = TrafficSimulation.Create(BusyMorning, capacity: 4);
        simulation.Generator.Start(simulation.Harness.Controller);
        await simulation.StepAsync(90);
        simulation.Generator.Statistics.Active.ShouldBeGreaterThan(0, "people are in the building when the outage starts");

        simulation.Harness.Controller.EmergencyStop();
        await simulation.StepAsync(30);
        simulation.Harness.Controller.ReturnToService();
        await simulation.RunUntilDoneAsync();

        simulation.Check.FirstMismatch.ShouldBeNull();
        var people = simulation.Generator.Statistics;
        (people.Delivered + people.Evacuated + people.Abandoned).ShouldBe(80);
        people.Abandoned.ShouldBeGreaterThan(0, "people who appeared during the outage could not call");
        simulation.Tracker.Totals.Evacuated.ShouldBe(people.Evacuated);
        simulation.Tracker.Totals.Riding.ShouldBe(0);
    }
}
