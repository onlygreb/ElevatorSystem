using ElevatorSystem.Application.Passengers;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ElevatorSystem.Application.Tests.Passengers;

/// <summary>The passenger rules of ADR-0026, checked against a real elevator moved by hand.</summary>
public sealed class PassengerRegistryTests
{
    private readonly FakeTimeProvider _time = new(ControllerHarness.StartTime);
    private readonly Elevator _elevator;
    private readonly RecordingPassengerObserver _observer = new();
    private readonly PassengerRegistry _registry;

    public PassengerRegistryTests()
    {
        _elevator = new Elevator(Building.Default, _time);
        _registry = Registry(new PassengerOptions());
    }

    [Fact]
    public void Join_PutsThePassengerOnTheLowestFloor_Present()
    {
        var id = Guid.NewGuid();

        var outcome = _registry.Join(id);

        outcome.Succeeded.ShouldBeTrue();
        _registry.Find(id).ShouldBe(new PassengerDto(id, 1, false, null, null, false));
        _registry.Current.Floors[0].Present.ShouldBe(1);
        _observer.Events.ShouldHaveSingleItem().ShouldBeOfType<PassengerJoined>();
    }

    [Fact]
    public void Join_Again_ChangesNothing()
    {
        var id = Guid.NewGuid();
        _registry.Join(id);

        _registry.Join(id).Succeeded.ShouldBeTrue();

        _registry.Passengers.Count.ShouldBe(1);
        _observer.Events.Count.ShouldBe(1);
    }

    [Fact]
    public void Join_WhenTheBuildingIsFull_IsRefused()
    {
        var registry = Registry(new PassengerOptions { MaxPassengers = 2 });
        registry.Join(Guid.NewGuid());
        registry.Join(Guid.NewGuid());

        var outcome = registry.Join(Guid.NewGuid());

        outcome.Refusal.ShouldBe(PassengerRefusalReason.BuildingFull);
        registry.Passengers.Count.ShouldBe(2);
    }

    [Fact]
    public void Call_MarksThePassengerWaiting_InThatDirection()
    {
        var id = Joined();

        _registry.Call(id, Direction.Up).Succeeded.ShouldBeTrue();

        _registry.Find(id)!.Waiting.ShouldBe(CallDirection.Up);
        _registry.Current.Floors[0].ShouldBe(new FloorOccupancyDto(1, Waiting: 1, Present: 0));
    }

    [Fact]
    public void Call_DownFromTheLowestFloor_IsRefused()
    {
        var id = Joined();

        _registry.Call(id, Direction.Down).Refusal.ShouldBe(PassengerRefusalReason.InvalidRequest);
        _registry.Find(id)!.Waiting.ShouldBeNull();
    }

    [Fact]
    public void Board_WithTheDoorOpenOnTheirFloor_PutsThemInTheCar_AndRecordsTheWait()
    {
        var id = Joined();
        _registry.Call(id, Direction.Up);
        _time.Advance(TimeSpan.FromSeconds(6));
        _elevator.OpenDoor();

        _registry.Board(id).Succeeded.ShouldBeTrue();

        _registry.Find(id).ShouldBe(new PassengerDto(id, 1, true, null, null, false));
        _registry.Current.PassengersInCar.ShouldBe(1);
        _registry.Current.AverageWaitTime.ShouldBe(TimeSpan.FromSeconds(6));
        _observer.Events.OfType<PassengerBoarded>().Single().WaitTime.ShouldBe(TimeSpan.FromSeconds(6));
    }

    [Fact]
    public void Board_WithTheDoorClosed_OrOpenOnAnotherFloor_IsRefused()
    {
        var id = Joined();

        _registry.Board(id).Refusal.ShouldBe(PassengerRefusalReason.DoorNotOpenHere);
        MoveTo(3);
        _elevator.OpenDoor();
        _registry.Board(id).Refusal.ShouldBe(PassengerRefusalReason.DoorNotOpenHere);
        _observer.Events.OfType<PassengerRefused>().Count().ShouldBe(2);
    }

    [Fact]
    public void Board_WhenTheCarIsFull_IsRefused()
    {
        var registry = Registry(new PassengerOptions { CarCapacity = 2 });
        _elevator.OpenDoor();
        var riders = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        riders.ForEach(id => registry.Join(id));

        registry.Board(riders[0]).Succeeded.ShouldBeTrue();
        registry.Board(riders[1]).Succeeded.ShouldBeTrue();

        registry.Board(riders[2]).Refusal.ShouldBe(PassengerRefusalReason.CarFull);
        registry.Current.PassengersInCar.ShouldBe(2);
    }

    [Fact]
    public void ChooseFloor_NeedsThePassengerInTheCar()
    {
        var id = Joined();

        _registry.ChooseFloor(id, new Floor(5)).Refusal.ShouldBe(PassengerRefusalReason.NotInCar);
        _elevator.OpenDoor();
        _registry.Board(id);
        _registry.ChooseFloor(id, new Floor(5)).Succeeded.ShouldBeTrue();
        _registry.ChooseFloor(id, new Floor(42)).Refusal.ShouldBe(PassengerRefusalReason.InvalidRequest);

        _registry.Find(id)!.Destination.ShouldBe(5);
    }

    [Fact]
    public void Leave_WithTheDoorOpen_PutsThemOnThatFloor_AndCountsARide()
    {
        var id = Riding();
        MoveTo(4);
        _elevator.OpenDoor();

        _registry.Leave(id).Succeeded.ShouldBeTrue();

        _registry.Find(id).ShouldBe(new PassengerDto(id, 4, false, null, null, false));
        _registry.Current.PassengersDelivered.ShouldBe(1);
        _registry.Current.Floors[3].Present.ShouldBe(1);
    }

    [Fact]
    public void Leave_OnTheFloorTheyBoarded_IsNotARide()
    {
        var id = Riding();

        _registry.Leave(id).Succeeded.ShouldBeTrue();

        _registry.Current.PassengersDelivered.ShouldBe(0);
    }

    [Fact]
    public void Leave_WithTheDoorClosed_IsRefused_AndTheyRideOn()
    {
        var id = Riding();
        MoveTo(2);

        _registry.Leave(id).Refusal.ShouldBe(PassengerRefusalReason.DoorClosed);
        _registry.Find(id)!.InCar.ShouldBeTrue();
        _registry.Find(id)!.Floor.ShouldBe(2, "a rider is wherever the car is");
    }

    [Fact]
    public void GoingOutOfService_LetsTheRidersOutWhereTheCarStopped()
    {
        var id = Riding();
        MoveTo(6);
        var stop = _elevator.TakeOutOfService(OutOfServiceReason.EmergencyStop);

        foreach (var elevatorEvent in stop.Events)
        {
            _registry.Publish(elevatorEvent);
        }

        _registry.Find(id).ShouldBe(new PassengerDto(id, 6, false, null, null, false));
        _observer.Events.OfType<PassengerLeft>().Single().Floor.ShouldBe(new Floor(6));
    }

    [Fact]
    public void People_WhoDoNothingForTooLong_LeaveTheBuilding_SimulatedOnesStay()
    {
        var registry = Registry(new PassengerOptions { IdleTimeout = TimeSpan.FromMinutes(5) });
        var person = Guid.NewGuid();
        var simulated = Guid.NewGuid();
        registry.Join(person);
        registry.Join(simulated, simulated: true);

        _time.Advance(TimeSpan.FromMinutes(6));
        registry.Publish(new DoorsOpened(_time.GetUtcNow(), new Floor(1)));

        registry.Find(person).ShouldBeNull();
        registry.Find(simulated).ShouldNotBeNull();
    }

    [Fact]
    public void RemoveSimulated_RemovesOnlySimulatedPassengers_WhereverTheyAre()
    {
        var person = Joined();
        _elevator.OpenDoor();
        var riding = Guid.NewGuid();
        _registry.Join(riding, simulated: true);
        _registry.Board(riding);
        _registry.Join(Guid.NewGuid(), simulated: true);

        _registry.RemoveSimulated().ShouldBe(2);

        _registry.Passengers.ShouldHaveSingleItem().PassengerId.ShouldBe(person);
        _registry.Current.PassengersInCar.ShouldBe(0);
    }

    [Fact]
    public void AStepByAnUnknownPassenger_IsRefused()
    {
        _registry.Board(Guid.NewGuid()).Refusal.ShouldBe(PassengerRefusalReason.UnknownPassenger);
        _observer.Events.ShouldHaveSingleItem().ShouldBeOfType<PassengerRefused>().Action.ShouldBe(PassengerAction.Board);
    }

    [Fact]
    public void TheObserver_HearsEveryChangeOfTheCounts()
    {
        var id = Joined();
        _elevator.OpenDoor();
        _registry.Board(id);

        _observer.Counts.Select(counts => counts.PassengersInCar).ShouldBe([0, 1]);
    }

    private PassengerRegistry Registry(PassengerOptions options) =>
        new(_elevator, options, _time, NullLogger<PassengerRegistry>.Instance, _observer);

    private Guid Joined()
    {
        var id = Guid.NewGuid();
        _registry.Join(id);
        return id;
    }

    private Guid Riding()
    {
        var id = Joined();
        _elevator.OpenDoor();
        _registry.Board(id).Succeeded.ShouldBeTrue();
        return id;
    }

    private void MoveTo(int floor)
    {
        _elevator.CloseDoor();
        while (_elevator.CurrentFloor.Number < floor)
        {
            _elevator.MoveUp();
        }

        while (_elevator.CurrentFloor.Number > floor)
        {
            _elevator.MoveDown();
        }

        if (_elevator.State != ElevatorState.Idle)
        {
            _elevator.OpenDoor();
            _elevator.CloseDoor();
        }
    }
}

/// <summary>Keeps every passenger event and every change of the counts, in order.</summary>
internal sealed class RecordingPassengerObserver : IPassengerObserver
{
    private readonly Lock _gate = new();
    private readonly List<PassengerEvent> _events = [];
    private readonly List<BuildingOccupancyDto> _counts = [];

    public IReadOnlyList<PassengerEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    public IReadOnlyList<BuildingOccupancyDto> Counts
    {
        get
        {
            lock (_gate)
            {
                return [.. _counts];
            }
        }
    }

    public void PassengerChanged(PassengerEvent passengerEvent)
    {
        lock (_gate)
        {
            _events.Add(passengerEvent);
        }
    }

    public void OccupancyChanged(BuildingOccupancyDto occupancy)
    {
        lock (_gate)
        {
            _counts.Add(occupancy);
        }
    }
}
