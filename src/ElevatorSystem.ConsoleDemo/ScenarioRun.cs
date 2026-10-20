using System.Globalization;
using ElevatorSystem.Application.Passengers;
using ElevatorSystem.Application.Simulation;
using ElevatorSystem.Contracts;

namespace ElevatorSystem.ConsoleDemo;

/// <summary>Runs a demo scenario (ADR-0027) so the counts move without typing, then sums up.</summary>
internal sealed class ScenarioRun(DemoScenarioRunner runner, PassengerRegistry registry, ConsoleOccupancyView people, ConsoleOutput output)
{
    /// <param name="scenario">What the simulated passengers do.</param>
    /// <param name="durationSeconds">How long to run; null runs until every simulated passenger is done or Ctrl+C.</param>
    /// <param name="untilStopped">Wait for the end and print a summary; otherwise start it and return.</param>
    /// <param name="cancellationToken">Ctrl+C: ends the run early, still with the summary.</param>
    public async Task RunAsync(DemoScenario scenario, int? durationSeconds, bool untilStopped, CancellationToken cancellationToken)
    {
        output.Heading($"Demo scenario: {scenario}");
        var added = runner.Start(scenario);
        output.Narrate(string.Create(
            CultureInfo.InvariantCulture,
            $"{added} simulated passengers arrive on floor 1, call the car, walk in, choose a floor and walk out there ({registry.Options.CarCapacity} fit at once)."));
        if (!untilStopped)
        {
            output.Narrate("Type 'people' at any time for the counts on every floor.");
            return;
        }

        output.Narrate(durationSeconds is { } limit
            ? string.Create(CultureInfo.InvariantCulture, $"Running for at most {limit} s; Ctrl+C stops it sooner.")
            : "Running until everyone is done; Ctrl+C stops it sooner.");
        var deadline = durationSeconds is { } seconds ? TimeProvider.System.GetUtcNow().AddSeconds(seconds) : DateTimeOffset.MaxValue;
        try
        {
            while (runner.Active > 0 && TimeProvider.System.GetUtcNow() < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), TimeProvider.System, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ctrl+C: sum up.
        }

        output.Heading("Scenario summary");
        people.PrintTable(registry.Current);
        runner.StopAll();
    }
}
