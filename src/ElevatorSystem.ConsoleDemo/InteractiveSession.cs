using System.Globalization;
using ElevatorSystem.Application;
using ElevatorSystem.Application.Simulation;
using ElevatorSystem.Domain;

namespace ElevatorSystem.ConsoleDemo;

/// <summary>Reads commands from the console and applies them to the running elevator.</summary>
internal sealed class InteractiveSession(ElevatorController controller, SimulatedElevatorDrive drive, ConsoleOutput output)
{
    private const string Help = """
        Commands:
          call <floor> up|down   call the elevator from a floor
          go <floor>             choose a destination inside the car
          stop                   emergency stop (cancels every call)
          maintenance            take the elevator out of service
          resume                 return the elevator to service
          jam                    make the next trip get stuck, to see the watchdog react
          status                 show the elevator's state and queue
          help                   show this list
          quit                   exit
        """;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        output.Heading("Interactive elevator: floors 1-10, FIFO scheduling");
        Console.WriteLine(Help);

        var cancelled = Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, cancellationToken);
        while (true)
        {
            var read = Console.In.ReadLineAsync(cancellationToken).AsTask();
            if (await Task.WhenAny(read, cancelled) != read)
            {
                return;
            }

            var line = await read;
            if (line is null || !Execute(line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            {
                return;
            }
        }
    }

    /// <returns><see langword="false"/> when the user asked to quit.</returns>
    private bool Execute(string[] words)
    {
        switch (words)
        {
            case []:
                return true;
            case ["quit" or "exit"]:
                return false;
            case ["help"]:
                Console.WriteLine(Help);
                return true;
            case ["call", var floorText, var directionText] when TryParseFloor(floorText, out var floor) && TryParseDirection(directionText, out var direction):
                Report(controller.RequestElevator(floor, direction));
                return true;
            case ["go", var floorText] when TryParseFloor(floorText, out var floor):
                Report(controller.RequestDestination(floor));
                return true;
            case ["stop"]:
                controller.EmergencyStop();
                return true;
            case ["maintenance"]:
                controller.StartMaintenance();
                return true;
            case ["resume"]:
                var resumed = controller.ReturnToService();
                if (!resumed.Succeeded)
                {
                    output.Result($"not possible: {resumed.Error}");
                }

                return true;
            case ["jam"]:
                drive.SimulateStuckCar();
                output.Result("The next trip will get stuck.");
                return true;
            case ["status"]:
                PrintStatus();
                return true;
            default:
                output.Result("Unknown command; type 'help'.");
                return true;
        }
    }

    private void PrintStatus()
    {
        var snapshot = controller.Elevator.Snapshot();
        var reason = snapshot.OutOfServiceReason is { } outOfService ? $" ({outOfService})" : string.Empty;
        output.Result($"Floor {snapshot.CurrentFloor}, {snapshot.State}{reason}, {controller.Strategy.Name} scheduling.");
        output.Result(snapshot.PendingCalls.IsEmpty
            ? "No pending calls."
            : "Queue: " + string.Join("; ", snapshot.PendingCalls.Select((pending, index) => $"{index + 1}. {pending.Call} x{pending.RequestCount}")));
    }

    private void Report(RequestResult result)
    {
        if (!result.IsAccepted)
        {
            output.Result($"refused: {result.RejectionReason}");
        }
    }

    private static bool TryParseFloor(string text, out int floor) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out floor);

    private static bool TryParseDirection(string text, out Direction direction)
    {
        direction = text.Equals("up", StringComparison.OrdinalIgnoreCase) ? Direction.Up : Direction.Down;
        return text.Equals("up", StringComparison.OrdinalIgnoreCase) || text.Equals("down", StringComparison.OrdinalIgnoreCase);
    }
}
