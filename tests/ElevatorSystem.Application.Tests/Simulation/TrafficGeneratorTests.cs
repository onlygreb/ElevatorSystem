using ElevatorSystem.Application.Occupancy;
using ElevatorSystem.Application.Simulation;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ElevatorSystem.Application.Tests.Simulation;

/// <summary>The generator's switch, its schedule in simulated time, and its caps.</summary>
public sealed class TrafficGeneratorTests
{
    private static readonly TrafficGeneratorOptions Enabled = new()
    {
        Enabled = true,
        ArrivalsPerMinute = 6,
        MaxActivePeople = 100_000,
        Seed = 11,
    };

    [Fact]
    public void Options_ByDefault_AreDisabled()
    {
        new TrafficGeneratorOptions().Enabled.ShouldBeFalse("the generator is a test tool and must be switched on explicitly (ADR-0017)");
    }

    [Fact]
    public async Task Start_WhenDisabled_PlacesNoCallsHoweverLongTimeRuns()
    {
        await using var simulation = TrafficSimulation.Create(new TrafficGeneratorOptions());

        var started = simulation.Generator.Start(simulation.Harness.Controller);
        simulation.Harness.Time.Advance(TimeSpan.FromHours(8));

        started.ShouldBeFalse();
        simulation.Generator.IsRunning.ShouldBeFalse();
        simulation.Generator.Statistics.Arrivals.ShouldBe(0);
        simulation.Elevator.Snapshot().PendingCalls.ShouldBeEmpty();
        simulation.Probe.Count.ShouldBe(0);
        simulation.GeneratorLog.WithId(1820).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Start_Twice_IsRefused()
    {
        await using var simulation = TrafficSimulation.Create(Enabled, startLoop: false);
        simulation.Generator.Start(simulation.Harness.Controller).ShouldBeTrue();

        Should.Throw<InvalidOperationException>(() => simulation.Generator.Start(simulation.Harness.Controller));
    }

    [Fact]
    public async Task Arrivals_InSimulatedTime_FollowTheSeededSchedule()
    {
        await using var simulation = TrafficSimulation.Create(Enabled, startLoop: false, intakeCapacity: 10_000);
        simulation.Generator.Start(simulation.Harness.Controller);

        AdvanceInSteps(simulation.Harness.Time, TimeSpan.FromHours(1), TimeSpan.FromSeconds(1));

        var expected = ArrivalsWithin(Enabled, TimeSpan.FromHours(1));
        simulation.Generator.Statistics.Arrivals.ShouldBe(expected);
        expected.ShouldBeInRange(300, 420, "about 6 per minute over 60 minutes");
    }

    [Fact]
    public async Task Arrivals_DoNotDependOnHowTimeIsAdvanced()
    {
        await using var stepped = TrafficSimulation.Create(Enabled, startLoop: false, intakeCapacity: 10_000);
        await using var jumped = TrafficSimulation.Create(Enabled, startLoop: false, intakeCapacity: 10_000);
        stepped.Generator.Start(stepped.Harness.Controller);
        jumped.Generator.Start(jumped.Harness.Controller);

        AdvanceInSteps(stepped.Harness.Time, TimeSpan.FromMinutes(30), TimeSpan.FromMilliseconds(250));
        jumped.Harness.Time.Advance(TimeSpan.FromMinutes(30));

        jumped.Generator.Statistics.Arrivals.ShouldBe(stepped.Generator.Statistics.Arrivals);
        jumped.Generator.Statistics.Arrivals.ShouldBe(ArrivalsWithin(Enabled, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public async Task Calls_WithTheSameSeed_AreTheSameCallsAtTheSameTimes()
    {
        var first = await RecordHallCallsAsync(Enabled);
        var again = await RecordHallCallsAsync(Enabled);
        var other = await RecordHallCallsAsync(Enabled with { Seed = 12 });

        first.Count.ShouldBeGreaterThan(100);
        again.ShouldBe(first);
        other.ShouldNotBe(first);
    }

    [Fact]
    public async Task Calls_ArePlacedWhereAndWhenTheScheduleSays()
    {
        var calls = await RecordHallCallsAsync(Enabled);

        var schedule = new ArrivalSchedule(Building.Default, Enabled);
        var due = ControllerHarness.StartTime;
        foreach (var (requestedAt, call) in calls)
        {
            var arrival = schedule.Next();
            due += arrival.Delay;
            call.ShouldBe(new HallCall(arrival.Origin, arrival.Direction));
            (requestedAt - due).ShouldBeInRange(TimeSpan.Zero, StepForRecording, "the press happens at the first timer tick at or after the arrival");
        }
    }

    [Fact]
    public async Task MaxActivePeople_IsNeverExceeded()
    {
        await using var simulation = TrafficSimulation.Create(Enabled with { MaxActivePeople = 3 }, startLoop: false, intakeCapacity: 10_000);
        simulation.Generator.Start(simulation.Harness.Controller);

        simulation.Harness.Time.Advance(TimeSpan.FromMinutes(10));

        var statistics = simulation.Generator.Statistics;
        statistics.Arrivals.ShouldBe(3, "nobody is served while the loop is stopped, so nobody leaves");
        statistics.Active.ShouldBe(3);
        statistics.Skipped.ShouldBe(ArrivalsWithin(Enabled, TimeSpan.FromMinutes(10)) - 3);
    }

    [Fact]
    public async Task MaxPeople_PlacesThatManyAndStops()
    {
        await using var simulation = TrafficSimulation.Create(Enabled with { MaxPeople = 5 }, startLoop: false);
        simulation.Generator.Start(simulation.Harness.Controller);

        simulation.Harness.Time.Advance(TimeSpan.FromHours(2));

        simulation.Generator.Statistics.Arrivals.ShouldBe(5);
        simulation.Generator.IsRunning.ShouldBeFalse();
        simulation.GeneratorLog.WithId(1824).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Stop_EndsNewArrivals()
    {
        await using var simulation = TrafficSimulation.Create(Enabled, startLoop: false, intakeCapacity: 10_000);
        simulation.Generator.Start(simulation.Harness.Controller);
        simulation.Harness.Time.Advance(TimeSpan.FromMinutes(10));
        var arrivals = simulation.Generator.Statistics.Arrivals;

        simulation.Generator.Stop();
        simulation.Harness.Time.Advance(TimeSpan.FromMinutes(10));

        simulation.Generator.Statistics.Arrivals.ShouldBe(arrivals);
        simulation.Generator.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task Arrival_WhileOutOfService_GivesUpWithoutBeingCounted()
    {
        await using var simulation = TrafficSimulation.Create(Enabled with { MaxPeople = 3 });
        simulation.Harness.Controller.StartMaintenance();
        simulation.Generator.Start(simulation.Harness.Controller);

        simulation.Harness.Time.Advance(TimeSpan.FromHours(1));

        simulation.Generator.Statistics.Abandoned.ShouldBe(3);
        simulation.Generator.Statistics.Active.ShouldBe(0);
        simulation.Tracker.Totals.PeopleCounted.ShouldBe(0);
    }

    [Theory]
    [InlineData(0, 20, null)]
    [InlineData(-1, 20, null)]
    [InlineData(double.NaN, 20, null)]
    [InlineData(6, 0, null)]
    [InlineData(6, 20, -1)]
    public void Constructor_WithOptionsOutOfRange_IsRefused(double arrivalsPerMinute, int maxActivePeople, int? maxPeople)
    {
        var options = new TrafficGeneratorOptions { ArrivalsPerMinute = arrivalsPerMinute, MaxActivePeople = maxActivePeople, MaxPeople = maxPeople };

        Should.Throw<ArgumentOutOfRangeException>(() => new TrafficGenerator(
            options, new OccupancyOptions(), new FakeTimeProvider(), NullLogger<TrafficGenerator>.Instance));
    }

    private static readonly TimeSpan StepForRecording = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Every first hall call of a 20-minute run, in the order the loop took them in. The loop starts
    /// only afterwards, so it takes them all in one go, before anyone boards or calls again.
    /// </summary>
    private static async Task<List<(DateTimeOffset RequestedAt, ElevatorCall Call)>> RecordHallCallsAsync(TrafficGeneratorOptions options)
    {
        await using var simulation = TrafficSimulation.Create(options, startLoop: false, intakeCapacity: 10_000);
        simulation.Generator.Start(simulation.Harness.Controller);
        AdvanceInSteps(simulation.Harness.Time, TimeSpan.FromMinutes(20), StepForRecording);
        var arrivals = simulation.Generator.Statistics.Arrivals;

        simulation.Harness.Start();
        await simulation.Probe.WaitUntilAsync(() => HallRequests(simulation.Probe).Count() >= arrivals, $"{arrivals} hall calls");

        return [.. HallRequests(simulation.Probe).Take(arrivals).Select(request => (request.RequestedAt, request.Call))];
    }

    private static IEnumerable<PassengerRequest> HallRequests(EventProbe probe) => probe.Events
        .Select(e => e switch
        {
            CallQueued queued => queued.Request,
            CallMerged merged => merged.Request,
            _ => null,
        })
        .OfType<PassengerRequest>()
        .Where(request => request.Call is HallCall);

    private static int ArrivalsWithin(TrafficGeneratorOptions options, TimeSpan duration)
    {
        var schedule = new ArrivalSchedule(Building.Default, options);
        var elapsed = TimeSpan.Zero;
        var count = -1;
        while (elapsed <= duration)
        {
            elapsed += schedule.Next().Delay;
            count++;
        }

        return count;
    }

    private static void AdvanceInSteps(FakeTimeProvider time, TimeSpan duration, TimeSpan step)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += step)
        {
            time.Advance(step);
        }
    }
}
