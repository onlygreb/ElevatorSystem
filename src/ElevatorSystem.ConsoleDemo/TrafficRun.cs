using System.Globalization;
using ElevatorSystem.Application;
using ElevatorSystem.Application.Occupancy;
using ElevatorSystem.Application.Simulation;

namespace ElevatorSystem.ConsoleDemo;

/// <summary>Runs the traffic generator so the passenger counts move without typing, then sums up.</summary>
internal sealed class TrafficRun(
    ElevatorController controller,
    TrafficGenerator generator,
    OccupancyTracker tracker,
    ConsoleOccupancyView people,
    ConsoleOutput output,
    TrafficGeneratorOptions options)
{
    /// <param name="durationSeconds">How long to run; null runs until cancelled.</param>
    /// <param name="untilStopped">Wait for the end and print a summary; otherwise start the traffic and return.</param>
    /// <param name="cancellationToken">Ctrl+C: ends the run early, still with the summary.</param>
    public async Task RunAsync(int? durationSeconds, bool untilStopped, CancellationToken cancellationToken)
    {
        output.Heading("Simulated traffic: a test tool standing in for passengers (ADR-0017)");
        output.Narrate(string.Create(
            CultureInfo.InvariantCulture,
            $"About {options.ArrivalsPerMinute:0} people a minute appear on random floors, at most {options.MaxActivePeople} at once (seed {options.Seed})."));
        output.Narrate(string.Create(
            CultureInfo.InvariantCulture,
            $"Each presses the hall button, boards when the door opens ({tracker.Current.CarCapacity} fit), presses a floor and gets off there."));
        generator.Start(controller);
        if (!untilStopped)
        {
            output.Narrate("Type 'people' at any time for the counts on every floor.");
            return;
        }

        output.Narrate(durationSeconds is { } limit
            ? string.Create(CultureInfo.InvariantCulture, $"Running for {limit} s; Ctrl+C stops it sooner.")
            : "Press Ctrl+C to stop.");
        try
        {
            var runFor = durationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : Timeout.InfiniteTimeSpan;
            await Task.Delay(runFor, TimeProvider.System, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ctrl+C: stop the traffic and sum up.
        }

        generator.Stop();
        var statistics = generator.Statistics;
        output.Heading("Traffic summary");
        output.Result(string.Create(
            CultureInfo.InvariantCulture,
            $"{statistics.Arrivals} people appeared ({statistics.Skipped} more were skipped because the building was full), {statistics.Delivered} reached their floor, {statistics.Active} are still on their way."));
        people.PrintTable(tracker.Current);
    }
}
