using System.Globalization;
using ElevatorSystem.Application;
using ElevatorSystem.Application.Passengers;
using ElevatorSystem.Application.Simulation;
using ElevatorSystem.Domain;

namespace ElevatorSystem.ConsoleDemo;

/// <summary>Reads commands from the console: you are a passenger, and you can also press any button or run scenarios.</summary>
internal sealed class InteractiveSession(
    ElevatorController controller,
    SimulatedElevatorDrive drive,
    PassengerService passengers,
    DemoScenarioRunner scenarios,
    ConsoleOccupancyView people,
    ConsoleOutput output)
{
    private const string Help = """
        You are a passenger:
          arrive                 walk into the building, on floor 1
          press up|down          press the button on your floor
          enter                  walk into the car (its door must be open on your floor)
          choose <floor>         choose a floor inside the car
          exit                   walk out of the car (its door must be open)
        Any button, without a passenger:
          call <floor> up|down   call the elevator from a floor
          go <floor>             press a floor button inside the car
        Demo scenarios:
          scenario <name>        morning, lunch, busy or huge: simulated passengers arrive on floor 1
          scenario stop          stop them and let them leave
        Service:
          stop                   emergency stop (cancels every call)
          maintenance            take the elevator out of service
          resume                 return the elevator to service
          jam                    make the next trip get stuck, to see the watchdog react
        Information:
          status                 show the elevator's state and queue
          people                 show how many people wait, ride and are present on each floor
          help                   show this list
          quit                   exit
        """;

    private readonly Guid _me = Guid.NewGuid();

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
            case ["quit"]:
                return false;
            case ["help"]:
                Console.WriteLine(Help);
                return true;
            case ["arrive"]:
                Report(passengers.Join(_me));
                return true;
            case ["press", var directionText] when TryParseDirection(directionText, out var direction):
                Report(passengers.CallElevator(_me, direction));
                return true;
            case ["enter"]:
                Report(passengers.Board(_me));
                return true;
            case ["choose", var floorText] when TryParseFloor(floorText, out var floor):
                Report(passengers.ChooseFloor(_me, new Floor(floor)));
                return true;
            case ["exit"]:
                Report(passengers.Leave(_me));
                return true;
            case ["call", var floorText, var directionText] when TryParseFloor(floorText, out var floor) && TryParseDirection(directionText, out var direction):
                Report(controller.RequestElevator(floor, direction));
                return true;
            case ["go", var floorText] when TryParseFloor(floorText, out var floor):
                Report(controller.RequestDestination(floor));
                return true;
            case ["scenario", "stop"]:
                output.Result($"{scenarios.StopAll()} simulated passenger(s) left.");
                return true;
            case ["scenario", var name] when DemoApp.ParseScenario(name) is { } scenario:
                output.Result($"{scenarios.Start(scenario)} simulated passengers are arriving on floor 1.");
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
            case ["people"]:
                people.PrintTable(passengers.Registry.Current);
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
        if (controller.DoorsCloseAt is { } closesAt)
        {
            var left = closesAt - TimeProvider.System.GetUtcNow();
            output.Result(string.Create(CultureInfo.InvariantCulture, $"The door closes in {Math.Max(0, left.TotalSeconds):0.0} s."));
        }

        output.Result(snapshot.PendingCalls.IsEmpty
            ? "No pending calls."
            : "Queue: " + string.Join("; ", snapshot.PendingCalls.Select((pending, index) => $"{index + 1}. {pending.Call} x{pending.RequestCount}")));
        if (passengers.Registry.Find(_me) is { } me)
        {
            output.Result(me.InCar
                ? $"You are in the car{(me.Destination is { } destination ? $", going to floor {destination}" : string.Empty)}."
                : $"You are on floor {me.Floor}{(me.Waiting is { } waiting ? $", waiting to go {waiting.ToString().ToLowerInvariant()}" : string.Empty)}.");
        }
    }

    private void Report(PassengerOutcome outcome)
    {
        if (!outcome.Succeeded)
        {
            output.Result($"not possible: {outcome.Refusal}");
        }
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
