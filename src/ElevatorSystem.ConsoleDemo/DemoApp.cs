using System.Globalization;
using ElevatorSystem.Application;
using ElevatorSystem.Application.Occupancy;
using ElevatorSystem.Application.Simulation;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Scheduling;
using Microsoft.Extensions.Logging;

namespace ElevatorSystem.ConsoleDemo;

/// <summary>Wires the elevator core together and runs the scripted tour, an interactive session or simulated traffic.</summary>
internal static class DemoApp
{
    private static readonly ElevatorControllerOptions DemoOptions = new()
    {
        TravelTimePerFloor = TimeSpan.FromMilliseconds(500),
        DoorOpenDuration = TimeSpan.FromSeconds(1),
        StuckTolerance = 3,
    };

    private static readonly OccupancyOptions Occupancy = new();

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        if (!TryReadNumber(args, "--seed", out var seed) || !TryReadNumber(args, "--duration", out var duration))
        {
            PrintUsage();
            return 2;
        }

        var interactive = args.Contains("--interactive") || args.Contains("-i");
        var verbose = args.Contains("--verbose") || args.Contains("-v");
        var traffic = new TrafficGeneratorOptions
        {
            Enabled = args.Contains("--traffic") || args.Contains("-t"),
            ArrivalsPerMinute = 12,
            MaxActivePeople = 20,
            Seed = seed ?? new TrafficGeneratorOptions().Seed,
        };
        var time = TimeProvider.System;

        // The controller logs every action; by default only warnings and errors are shown here,
        // because the event lines below already narrate each action.
        using var loggerFactory = LoggerFactory.Create(logging => logging
            .AddSimpleConsole(console =>
            {
                console.SingleLine = true;
                console.TimestampFormat = "HH:mm:ss.fff ";
            })
            .SetMinimumLevel(verbose ? LogLevel.Information : LogLevel.Warning));

        var output = new ConsoleOutput(time);
        var events = new ConsoleEventSink(output);
        var people = new ConsoleOccupancyView(output);
        var tracker = new OccupancyTracker(Building.Default, Occupancy, time, loggerFactory.CreateLogger<OccupancyTracker>(), people);
        using var generator = new TrafficGenerator(traffic, Occupancy, time, loggerFactory.CreateLogger<TrafficGenerator>());
        var drive = new SimulatedElevatorDrive(DemoOptions.TravelTimePerFloor, time);
        var controller = new ElevatorController(
            new Elevator(Building.Default, time),
            new FifoSchedulingStrategy(),
            drive,
            // The event line first, then the tracker, so a change in the counts prints after the event that caused it.
            new CompositeElevatorEventSink([events, tracker, generator], loggerFactory.CreateLogger<CompositeElevatorEventSink>()),
            DemoOptions,
            time,
            loggerFactory.CreateLogger<ElevatorController>());

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };

        var loop = controller.ProcessRequests(shutdown.Token);
        try
        {
            if (traffic.Enabled)
            {
                // With -i the traffic runs in the background while commands are typed.
                await new TrafficRun(controller, generator, tracker, people, output, traffic)
                    .RunAsync(duration, untilStopped: !interactive, shutdown.Token);
            }

            if (interactive)
            {
                await new InteractiveSession(controller, drive, tracker, people, output).RunAsync(shutdown.Token);
            }
            else if (!traffic.Enabled)
            {
                await new ScriptedTour(controller, drive, events, tracker, people, output, DemoOptions).RunAsync(shutdown.Token);
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            output.Narrate("Stopped.");
        }
        catch (TimeoutException)
        {
            output.Result("The tour expected an event that did not happen within 30 s; stopping.");
            return 1;
        }
        finally
        {
            generator.Stop();
            await shutdown.CancelAsync();
            await loop;
        }

        return 0;
    }

    /// <summary>Reads an optional whole number following <paramref name="name"/>; false when it is present but not a number.</summary>
    private static bool TryReadNumber(string[] args, string name, out int? value)
    {
        value = null;
        var index = Array.IndexOf(args, name);
        if (index < 0)
        {
            return true;
        }

        if (index + 1 < args.Length && int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= 0)
        {
            value = number;
            return true;
        }

        Console.Error.WriteLine($"{name} needs a whole number.");
        return false;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Elevator System console demo: one elevator, floors 1-10, FIFO scheduling.

            Usage: dotnet run --project src/ElevatorSystem.ConsoleDemo -- [options]

              (no options)        run the narrated tour of the elevator's behaviour
              -i, --interactive   drive the elevator yourself (type 'help' for commands)
              -t, --traffic       simulated passengers call the elevator, so the counts move on their own
                                  (a test tool, ADR-0017); add -i to type commands meanwhile
                --seed <n>        the traffic's random seed: the same seed gives the same passengers
                --duration <s>    stop the traffic after this many seconds (default: until Ctrl+C)
              -v, --verbose       also show the controller's information-level logs
              -h, --help          show this help

            Passengers are counted from their calls (ADR-0017): each hall call is one person waiting,
            the car carries 8, and people left behind by a full car wait for the next one.
            """);
    }
}
