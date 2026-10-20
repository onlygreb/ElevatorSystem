using System.Globalization;
using ElevatorSystem.Application;
using ElevatorSystem.Application.Passengers;
using ElevatorSystem.Application.Simulation;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;

namespace ElevatorSystem.ConsoleDemo;

/// <summary>A narrated walk through the elevator's behaviour, one capability per step.</summary>
internal sealed class ScriptedTour(
    ElevatorController controller,
    SimulatedElevatorDrive drive,
    ConsoleEventSink events,
    PassengerService passengers,
    ConsoleOccupancyView people,
    ConsoleOutput output,
    ElevatorControllerOptions options)
{
    private const int BurstSize = 200;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        output.Heading("Elevator System: one car, floors 1-10, FIFO scheduling");
        output.Narrate(string.Create(
            CultureInfo.InvariantCulture,
            $"Travel {options.TravelTimePerFloor.TotalSeconds:0.0} s per floor, doors close {options.DoorOpenDuration.TotalSeconds:0.0} s after a call, watchdog after {options.StuckTimeout.TotalSeconds:0.0} s."));
        output.Narrate(string.Create(
            CultureInfo.InvariantCulture,
            $"Everyone arrives on floor 1, and with nowhere to go the car waits there with its door open. It carries {passengers.Registry.Options.CarCapacity}."));

        output.Heading("1. Ana arrives on floor 1, walks straight in and chooses floor 7");
        var ana = Guid.NewGuid();
        await DoorOpenAsync(1, cancellationToken);
        passengers.Join(ana);
        passengers.Board(ana);
        var anaArrives = DoorOpenAsync(7, cancellationToken);
        passengers.ChooseFloor(ana, new Floor(7));
        await anaArrives;
        passengers.Leave(ana);

        output.Heading("2. Bruno arrives on floor 1 and calls the car down from floor 7");
        output.Narrate("The car waits open at floor 7; Bruno's call starts the door countdown, then it comes down.");
        var bruno = Guid.NewGuid();
        passengers.Join(bruno);
        var pickedUp = DoorOpenAsync(1, cancellationToken);
        passengers.CallElevator(bruno, Direction.Up);
        await pickedUp;
        passengers.Board(bruno);
        var brunoArrives = DoorOpenAsync(4, cancellationToken);
        passengers.ChooseFloor(bruno, new Floor(4));
        await brunoArrives;
        output.Narrate("Bruno stays in the car: whoever does not walk out rides on until they choose again.");
        var backDown = DoorOpenAsync(2, cancellationToken);
        passengers.ChooseFloor(bruno, new Floor(2));
        await backDown;
        passengers.Leave(bruno);

        output.Heading("3. The same button pressed three times becomes one call");
        var nineServed = Served(new HallCall(new Floor(9), Direction.Down), cancellationToken);
        for (var press = 0; press < 3; press++)
        {
            Report(controller.RequestElevator(9, Direction.Down));
        }

        await nineServed;

        output.Heading("4. Invalid requests are refused straight away");
        Report(controller.RequestElevator(11, Direction.Up), "call from floor 11");
        Report(controller.RequestElevator(10, Direction.Up), "call up from the top floor");
        Report(controller.RequestDestination(0), "destination floor 0");

        output.Heading($"5. {BurstSize} requests arrive at the same moment from parallel tasks");
        await RunBurstAsync(cancellationToken);

        output.Heading("6. Emergency stop: the car stops at once and every call is cancelled");
        controller.EmergencyStop();
        Report(controller.RequestElevator(3, Direction.Up), "call while stopped");
        controller.ReturnToService();

        output.Heading("7. The car jams between floors and the watchdog reacts");
        drive.SimulateStuckCar();
        var fault = events.WaitFor(e => e is ElevatorTakenOutOfService { Reason: OutOfServiceReason.Fault }, cancellationToken);
        Report(controller.RequestDestination(6));
        await fault;
        output.Narrate("No arrival within the watchdog limit, so the car was taken out of service. An operator returns it:");
        controller.ReturnToService();

        output.Heading("Where everyone is now");
        people.PrintTable(passengers.Registry.Current);

        output.Heading("Tour complete");
        output.Narrate("Run with --interactive to ride yourself, or --scenario morning|lunch|busy to watch a crowd.");
    }

    /// <summary>Completes once the door is open at <paramref name="floor"/>, at once if it already is.</summary>
    private Task DoorOpenAsync(int floor, CancellationToken cancellationToken)
    {
        var opened = events.WaitFor(e => e is DoorsOpened door && door.Floor == new Floor(floor), cancellationToken);
        var snapshot = controller.Elevator.Snapshot();
        return snapshot.State == ElevatorState.DoorOpen && snapshot.CurrentFloor == new Floor(floor) ? Task.CompletedTask : opened;
    }

    private async Task RunBurstAsync(CancellationToken cancellationToken)
    {
        events.QuietRequests = true;
        people.Quiet = true;
        var assigned = events.ExpectAssignments(BurstSize, cancellationToken);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, BurstSize)
            .Select(_ => Task.Run(
                async () =>
                {
                    await gate.Task.ConfigureAwait(false);
                    var goingUp = Random.Shared.Next(2) == 0;
                    var floor = goingUp ? Random.Shared.Next(1, 10) : Random.Shared.Next(2, 11);
                    return controller.RequestElevator(floor, goingUp ? Direction.Up : Direction.Down);
                },
                cancellationToken))
            .ToArray();

        gate.SetResult();
        var results = await Task.WhenAll(requests);
        await assigned;
        events.QuietRequests = false;
        people.Quiet = false;

        var snapshot = controller.Elevator.Snapshot();
        output.Result(string.Create(
            CultureInfo.InvariantCulture,
            $"{results.Count(result => result.IsAccepted)} of {BurstSize} accepted: {events.ServedOnArrival} served on the spot (the doors were open at their floor), the rest merged into {snapshot.PendingCalls.Length} pending calls (at most {Building.Default.MaxPendingCalls} can exist)."));
        output.Result(string.Create(
            CultureInfo.InvariantCulture,
            $"Slowest assignment: {events.SlowestAssignment.TotalMilliseconds:0.000} ms (budget: 100 ms)."));
    }

    private Task Served(ElevatorCall call, CancellationToken cancellationToken) =>
        events.WaitFor(e => e is CallServed served && served.Call.Call == call, cancellationToken);

    private void Report(RequestResult result, string? label = null)
    {
        if (!result.IsAccepted)
        {
            output.Result($"{label ?? "request"}: refused ({result.RejectionReason})");
        }
    }
}
