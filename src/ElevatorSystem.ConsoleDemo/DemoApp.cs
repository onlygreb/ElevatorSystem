using ElevatorSystem.Application;
using ElevatorSystem.Application.Simulation;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Scheduling;
using Microsoft.Extensions.Logging;

namespace ElevatorSystem.ConsoleDemo;

/// <summary>Wires the elevator core together and runs either the scripted tour or an interactive session.</summary>
internal static class DemoApp
{
    private static readonly ElevatorControllerOptions DemoOptions = new()
    {
        TravelTimePerFloor = TimeSpan.FromMilliseconds(500),
        DoorOpenDuration = TimeSpan.FromSeconds(1),
        StuckTolerance = 3,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
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
        var drive = new SimulatedElevatorDrive(DemoOptions.TravelTimePerFloor, time);
        var controller = new ElevatorController(
            new Elevator(Building.Default, time),
            new FifoSchedulingStrategy(),
            drive,
            events,
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
            if (interactive)
            {
                await new InteractiveSession(controller, drive, output).RunAsync(shutdown.Token);
            }
            else
            {
                await new ScriptedTour(controller, drive, events, output, DemoOptions).RunAsync(shutdown.Token);
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
            await shutdown.CancelAsync();
            await loop;
        }

        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Elevator System console demo: one elevator, floors 1-10, FIFO scheduling.

            Usage: dotnet run --project src/ElevatorSystem.ConsoleDemo -- [options]

              (no options)        run the narrated tour of the elevator's behaviour
              -i, --interactive   drive the elevator yourself (type 'help' for commands)
              -v, --verbose       also show the controller's information-level logs
              -h, --help          show this help
            """);
    }
}
