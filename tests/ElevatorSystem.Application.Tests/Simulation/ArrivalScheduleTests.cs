using ElevatorSystem.Application.Simulation;
using ElevatorSystem.Domain;

namespace ElevatorSystem.Application.Tests.Simulation;

public sealed class ArrivalScheduleTests
{
    private static readonly TrafficGeneratorOptions Options = new() { Enabled = true, ArrivalsPerMinute = 6, Seed = 11 };

    [Fact]
    public void Next_WithTheSameSeed_GivesTheSameArrivals()
    {
        var first = new ArrivalSchedule(Building.Default, Options);
        var second = new ArrivalSchedule(Building.Default, Options);

        var arrivals = Take(first, 500);

        arrivals.ShouldBe(Take(second, 500));
    }

    [Fact]
    public void Next_WithAnotherSeed_GivesOtherArrivals()
    {
        var first = new ArrivalSchedule(Building.Default, Options);
        var second = new ArrivalSchedule(Building.Default, Options with { Seed = 12 });

        var arrivals = Take(first, 50);

        arrivals.ShouldNotBe(Take(second, 50));
    }

    [Fact]
    public void Next_PicksFloorsInTheBuildingAndADestinationOtherThanTheOrigin()
    {
        var schedule = new ArrivalSchedule(Building.Default, Options);

        var arrivals = Take(schedule, 10_000);

        arrivals.ShouldAllBe(arrival => Building.Default.Contains(arrival.Origin) && Building.Default.Contains(arrival.Destination));
        arrivals.ShouldAllBe(arrival => arrival.Origin != arrival.Destination);
        arrivals.ShouldAllBe(arrival => (arrival.Direction == Direction.Up) == (arrival.Destination > arrival.Origin));
        arrivals.Select(arrival => arrival.Origin.Number).Distinct().Count().ShouldBe(10, "every floor is an origin");
        arrivals.Select(arrival => arrival.Destination.Number).Distinct().Count().ShouldBe(10, "every floor is a destination");
    }

    [Fact]
    public void Next_GapsAverageToTheConfiguredRate()
    {
        var schedule = new ArrivalSchedule(Building.Default, Options);

        var meanGap = TimeSpan.FromTicks((long)Take(schedule, 20_000).Average(arrival => arrival.Delay.Ticks));

        meanGap.TotalSeconds.ShouldBe(10, tolerance: 0.3, "6 arrivals per minute is one every 10 s on average");
    }

    private static List<SimulatedArrival> Take(ArrivalSchedule schedule, int count) =>
        [.. Enumerable.Range(0, count).Select(_ => schedule.Next())];
}
