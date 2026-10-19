using System.Globalization;
using ElevatorSystem.Application;
using ElevatorSystem.Application.Occupancy;
using ElevatorSystem.Application.Simulation;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;

namespace ElevatorSystem.ConsoleDemo;

/// <summary>A narrated walk through the elevator's behaviour, one capability per step.</summary>
internal sealed class ScriptedTour(
    ElevatorController controller,
    SimulatedElevatorDrive drive,
    ConsoleEventSink events,
    OccupancyTracker tracker,
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
            $"Travel {options.TravelTimePerFloor.TotalSeconds:0.0} s per floor, doors open {options.DoorOpenDuration.TotalSeconds:0.0} s, watchdog after {options.StuckTimeout.TotalSeconds:0.0} s."));
        output.Narrate(string.Create(
            CultureInfo.InvariantCulture,
            $"People are counted from their calls: each hall call is one person waiting, and the car carries {tracker.Current.CarCapacity} (the 'people' lines)."));

        output.Heading("1. A passenger on floor 7 calls the elevator to go down");
        var pickedUp = Served(new HallCall(new Floor(7), Direction.Down), cancellationToken);
        Report(controller.RequestElevator(7, Direction.Down));
        await pickedUp;

        output.Heading("2. They choose floor 2; meanwhile someone on floor 4 calls to go up");
        output.Narrate("FIFO: the car serves floor 2 first, passing floor 4 without stopping, then comes back up.");
        var fourServed = Served(new HallCall(new Floor(4), Direction.Up), cancellationToken);
        Report(controller.RequestDestination(2));
        Report(controller.RequestElevator(4, Direction.Up));
        await fourServed;

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
        output.Narrate("Riders were evacuated when the car stopped; people whose calls were cancelled are still waiting.");
        people.PrintTable(tracker.Current);

        output.Heading("Tour complete");
        output.Narrate("Run with --interactive to drive the elevator yourself.");
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
        output.Result($"Every hall call is a person: {ConsoleOccupancyView.Summary(tracker.Current)}.");
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
