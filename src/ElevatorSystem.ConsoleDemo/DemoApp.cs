using System.Globalization;
using ElevatorSystem.Application;
using ElevatorSystem.Application.Passengers;
using ElevatorSystem.Application.Simulation;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Scheduling;
using Microsoft.Extensions.Logging;

namespace ElevatorSystem.ConsoleDemo;

/// <summary>Wires the elevator core together and runs the scripted tour, an interactive session or a demo scenario.</summary>
internal static class DemoApp
{
    private static readonly ElevatorControllerOptions DemoOptions = new()
    {
        TravelTimePerFloor = TimeSpan.FromMilliseconds(500),
        DoorOpenDuration = TimeSpan.FromSeconds(1.5),
        StuckTolerance = 3,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        if (!TryReadNumber(args, "--seed", out var seed) || !TryReadNumber(args, "--duration", out var duration) || !TryReadScenario(args, out var scenario))
        {
            PrintUsage();
            return 2;
        }

        var interactive = args.Contains("--interactive") || args.Contains("-i");
        var verbose = args.Contains("--verbose") || args.Contains("-v");
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
        var elevator = new Elevator(Building.Default, time);
        var registry = new PassengerRegistry(elevator, new PassengerOptions(), time, loggerFactory.CreateLogger<PassengerRegistry>(), people);
        var drive = new SimulatedElevatorDrive(DemoOptions.TravelTimePerFloor, time);
        var controller = new ElevatorController(
            elevator,
            new FifoSchedulingStrategy(),
            drive,
            // The event line first, then the passengers, so a change in the counts prints after the event that caused it.
            new CompositeElevatorEventSink([events, registry], loggerFactory.CreateLogger<CompositeElevatorEventSink>()),
            DemoOptions,
            time,
            loggerFactory.CreateLogger<ElevatorController>());
        var passengers = new PassengerService(registry, controller, time);
        using var scenarios = new DemoScenarioRunner(
            passengers, elevator, new SimulationOptions { Enabled = true, Seed = seed }, time, loggerFactory.CreateLogger<DemoScenarioRunner>());

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };

        var loop = controller.ProcessRequests(shutdown.Token);
        try
        {
            if (scenario is { } chosen)
            {
                // With -i the scenario runs in the background while commands are typed.
                await new ScenarioRun(scenarios, registry, people, output).RunAsync(chosen, duration, untilStopped: !interactive, shutdown.Token);
            }

            if (interactive)
            {
                await new InteractiveSession(controller, drive, passengers, scenarios, people, output).RunAsync(shutdown.Token);
            }
            else if (scenario is null)
            {
                await new ScriptedTour(controller, drive, events, passengers, people, output, DemoOptions).RunAsync(shutdown.Token);
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
            scenarios.StopAll();
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

    /// <summary>Reads <c>--scenario morning|lunch|busy|huge</c>; <c>-t</c> is short for the busy day.</summary>
    private static bool TryReadScenario(string[] args, out DemoScenario? scenario)
    {
        scenario = args.Contains("-t") || args.Contains("--traffic") ? DemoScenario.BusyDay : null;
        var index = Array.IndexOf(args, "--scenario");
        if (index < 0)
        {
            return true;
        }

        scenario = index + 1 < args.Length ? ParseScenario(args[index + 1]) : null;
        if (scenario is null)
        {
            Console.Error.WriteLine("--scenario needs morning, lunch, busy or huge.");
        }

        return scenario is not null;
    }

    public static DemoScenario? ParseScenario(string text) => text.ToLowerInvariant() switch
    {
        "morning" => DemoScenario.MorningRush,
        "lunch" => DemoScenario.LunchBreak,
        "busy" => DemoScenario.BusyDay,
        "huge" => DemoScenario.HugeLoad,
        _ => null,
    };

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Elevator System console demo: one elevator, floors 1-10, FIFO scheduling.

            Usage: dotnet run --project src/ElevatorSystem.ConsoleDemo -- [options]

              (no options)                 run the narrated tour of the elevator's behaviour
              -i, --interactive            drive it yourself: arrive, call, walk in, choose a floor, walk out
              --scenario morning|lunch|busy|huge
                                           simulated passengers start on floor 1 and ride around (ADR-0027);
                                           add -i to type commands meanwhile; -t is short for busy
                --seed <n>                 the scenario's random seed: the same seed gives the same passengers
                --duration <s>             stop the scenario after this many seconds
              -v, --verbose                also show the controller's information-level logs
              -h, --help                   show this help

            Passengers walk in and out themselves (ADR-0026): everyone arrives on floor 1, the car waits
            with its door open when it has nowhere to go, and it carries 8 people at most.
            """);
    }
}
