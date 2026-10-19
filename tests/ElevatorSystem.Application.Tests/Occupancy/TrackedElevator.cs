using ElevatorSystem.Application.Occupancy;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;
using Microsoft.Extensions.Time.Testing;

namespace ElevatorSystem.Application.Tests.Occupancy;

/// <summary>
/// A domain <see cref="Elevator"/> driven by hand, with every event it produces handed straight to an
/// <see cref="OccupancyTracker"/>. No control loop and no waiting: each command is applied and counted
/// synchronously, so a test can script the exact sequence of events it needs.
/// </summary>
internal sealed class TrackedElevator
{
    public static readonly DateTimeOffset StartTime = new(2026, 1, 5, 8, 0, 0, TimeSpan.Zero);

    private TrackedElevator(int startFloor, int capacity, IOccupancyObserver? observer)
    {
        Time = new FakeTimeProvider(StartTime);
        Elevator = new Elevator(Building.Default, Time, new Floor(startFloor));
        Log = new RecordingLogger<OccupancyTracker>();
        Tracker = new OccupancyTracker(Building.Default, new OccupancyOptions { CarCapacity = capacity }, Time, Log, observer);
    }

    public FakeTimeProvider Time { get; }

    public Elevator Elevator { get; }

    public OccupancyTracker Tracker { get; }

    public RecordingLogger<OccupancyTracker> Log { get; }

    public OccupancyTotals Totals => Tracker.Totals;

    public BuildingOccupancyDto Current => Tracker.Current;

    public static TrackedElevator Create(int startFloor = 1, int capacity = 8, IOccupancyObserver? observer = null) =>
        new(startFloor, capacity, observer);

    public FloorOccupancyDto Floor(int number) => Current.Floors.Single(floor => floor.Floor == number);

    /// <summary>Someone on <paramref name="floor"/> presses the hall button.</summary>
    public TrackedElevator Call(int floor, Direction direction = Direction.Up) =>
        Request(new HallCall(new Floor(floor), direction));

    /// <summary>A rider presses <paramref name="floor"/> inside the car.</summary>
    public TrackedElevator Select(int floor) => Request(new CarCall(new Floor(floor)));

    /// <summary>Closes the door if it is open, then moves floor by floor to <paramref name="floor"/> without stopping.</summary>
    public TrackedElevator TravelTo(int floor)
    {
        Publish(Elevator.CloseDoor().Events);
        while (Elevator.CurrentFloor.Number != floor)
        {
            var result = Elevator.CurrentFloor.Number < floor ? Elevator.MoveUp() : Elevator.MoveDown();
            result.Succeeded.ShouldBeTrue($"the scripted trip to floor {floor} was refused: {result.Error}");
            Publish(result.Events);
        }

        return this;
    }

    /// <summary>Stops at <paramref name="floor"/> and opens the door there.</summary>
    public TrackedElevator StopAt(int floor)
    {
        TravelTo(floor);
        return OpenDoor();
    }

    public TrackedElevator OpenDoor() => Publish(Elevator.OpenDoor().Events);

    public TrackedElevator CloseDoor() => Publish(Elevator.CloseDoor().Events);

    public TrackedElevator TakeOutOfService(OutOfServiceReason reason = OutOfServiceReason.EmergencyStop) =>
        Publish(Elevator.TakeOutOfService(reason).Events);

    public TrackedElevator ReturnToService() => Publish(Elevator.ReturnToService().Events);

    public TrackedElevator Wait(TimeSpan duration)
    {
        Time.Advance(duration);
        return this;
    }

    public TrackedElevator Publish(IEnumerable<ElevatorEvent> events)
    {
        foreach (var elevatorEvent in events)
        {
            Tracker.Publish(elevatorEvent);
        }

        return this;
    }

    private TrackedElevator Request(ElevatorCall call) =>
        Publish(Elevator.AddRequest(new PassengerRequest(RequestId.New(Time), call, Time.GetUtcNow())).Events);
}
