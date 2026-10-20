using ElevatorSystem.Application.Passengers;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ElevatorSystem.Application.Tests.Passengers;

/// <summary>
/// What the registry tells its observer (the activity feed and the occupancy counts are built from it), and the edges
/// of its timing and counting rules (ADR-0026).
/// </summary>
public sealed class PassengerRegistryNotificationTests
{
    private readonly FakeTimeProvider _time = new(ControllerHarness.StartTime);
    private readonly Elevator _elevator;
    private readonly RecordingPassengerObserver _observer = new();
    private readonly PassengerRegistry _registry;

    public PassengerRegistryNotificationTests()
    {
        _elevator = new Elevator(Building.Default, _time);
        _registry = Registry(new PassengerOptions());
    }

    [Fact]
    public void Call_IsReported_WithItsDirectionAndRequest_AndTheCountsFollow()
    {
        var id = Joined();
        var request = Guid.NewGuid();
        var countsBefore = _observer.Counts.Count;

        _registry.Call(id, Direction.Up, request);

        var called = _observer.Events[^1].ShouldBeOfType<PassengerCalled>();
        called.Direction.ShouldBe(Direction.Up);
        called.RequestId.ShouldBe(request);
        called.Floor.ShouldBe(new Floor(1));
        _observer.Counts.Count.ShouldBe(countsBefore + 1);
        _observer.Counts[^1].Floors[0].Waiting.ShouldBe(1);
    }

    [Fact]
    public void CallingAgain_KeepsTheTimeOfTheFirstCall()
    {
        var id = Joined();
        _registry.Call(id, Direction.Up);
        _time.Advance(TimeSpan.FromSeconds(4));
        _registry.Call(id, Direction.Up);
        _time.Advance(TimeSpan.FromSeconds(2));
        _elevator.OpenDoor();

        _registry.Board(id);

        _observer.Events.OfType<PassengerBoarded>().Single().WaitTime.ShouldBe(TimeSpan.FromSeconds(6), "the wait runs from the first press");
    }

    [Fact]
    public void WithdrawCall_MakesThemPresentAgain_AndForgetsTheWait()
    {
        var id = Joined();
        _registry.Call(id, Direction.Up);
        var countsBefore = _observer.Counts.Count;

        _registry.WithdrawCall(id);
        _elevator.OpenDoor();
        _registry.Board(id);

        _registry.Current.AverageWaitTime.ShouldBeNull("a withdrawn call is no wait");
        _observer.Counts[countsBefore].Floors[0].ShouldBe(new FloorOccupancyDto(1, Waiting: 0, Present: 1));
    }

    [Fact]
    public void WithdrawCall_OfSomeoneNotWaiting_ChangesNothing()
    {
        var id = Joined();
        var countsBefore = _observer.Counts.Count;

        _registry.WithdrawCall(id);
        _registry.WithdrawCall(Guid.NewGuid());

        _observer.Counts.Count.ShouldBe(countsBefore);
    }

    [Fact]
    public void Join_WhenTheBuildingIsFull_IsReportedAsRefused()
    {
        var registry = Registry(new PassengerOptions { MaxPassengers = 1 });
        registry.Join(Guid.NewGuid());
        var turnedAway = Guid.NewGuid();

        registry.Join(turnedAway, simulated: true);

        var refused = _observer.Events[^1].ShouldBeOfType<PassengerRefused>();
        refused.PassengerId.ShouldBe(turnedAway);
        refused.Action.ShouldBe(PassengerAction.Join);
        refused.Reason.ShouldBe(PassengerRefusalReason.BuildingFull);
        refused.Simulated.ShouldBeTrue();
    }

    [Fact]
    public void Join_MakesRoomByLettingIdlePeopleGoFirst()
    {
        var registry = Registry(new PassengerOptions { MaxPassengers = 1, IdleTimeout = TimeSpan.FromMinutes(5) });
        var idle = Guid.NewGuid();
        registry.Join(idle);
        _time.Advance(TimeSpan.FromMinutes(6));

        registry.Join(Guid.NewGuid()).Succeeded.ShouldBeTrue();

        registry.Find(idle).ShouldBeNull();
        _observer.Events.OfType<PassengerRemoved>().Single().PassengerId.ShouldBe(idle);
    }

    [Fact]
    public void IdleTimeout_IsReachedOnlyAfterItHasFullyPassed()
    {
        var registry = Registry(new PassengerOptions { IdleTimeout = TimeSpan.FromMinutes(5) });
        var id = Guid.NewGuid();
        registry.Join(id);

        _time.Advance(TimeSpan.FromMinutes(5));
        registry.Publish(new DoorsOpened(_time.GetUtcNow(), new Floor(1)));
        registry.Find(id).ShouldNotBeNull("exactly the timeout is not yet too long");

        _time.Advance(TimeSpan.FromTicks(1));
        registry.Publish(new DoorsOpened(_time.GetUtcNow(), new Floor(1)));
        registry.Find(id).ShouldBeNull();
    }

    [Fact]
    public void DoorsOpening_WithNobodyIdle_DoesNotRecount()
    {
        Joined();
        var countsBefore = _observer.Counts.Count;

        _registry.Publish(new DoorsOpened(_time.GetUtcNow(), new Floor(1)));

        _observer.Counts.Count.ShouldBe(countsBefore);
    }

    [Fact]
    public void IdlePeople_LeavingWhenTheDoorsOpen_AreRecounted()
    {
        var registry = Registry(new PassengerOptions { IdleTimeout = TimeSpan.FromMinutes(5) });
        registry.Join(Guid.NewGuid());
        _time.Advance(TimeSpan.FromMinutes(6));
        var countsBefore = _observer.Counts.Count;

        registry.Publish(new DoorsOpened(_time.GetUtcNow(), new Floor(1)));

        _observer.Counts.Count.ShouldBe(countsBefore + 1);
        _observer.Counts[^1].Floors[0].Present.ShouldBe(0);
    }

    [Fact]
    public void ChooseFloor_IsReported_FromWhereTheCarIs()
    {
        var id = Riding();
        MoveTo(3);

        _registry.ChooseFloor(id, new Floor(8));

        var chose = _observer.Events[^1].ShouldBeOfType<PassengerChoseFloor>();
        chose.Destination.ShouldBe(new Floor(8));
        chose.Floor.ShouldBe(new Floor(3));
    }

    [Fact]
    public void ChooseFloor_WhileOutOfService_IsRefused()
    {
        var id = Riding();
        _elevator.TakeOutOfService(OutOfServiceReason.Maintenance);

        _registry.ChooseFloor(id, new Floor(8)).Refusal.ShouldBe(PassengerRefusalReason.OutOfService);
    }

    [Fact]
    public void ARidersRefusedStep_IsReportedWhereTheCarIs()
    {
        var id = Riding();
        MoveTo(4);

        var outcome = _registry.Leave(id);

        outcome.Floor.ShouldBe(new Floor(4));
        _observer.Events[^1].ShouldBeOfType<PassengerRefused>().Floor.ShouldBe(new Floor(4));
    }

    [Fact]
    public void ASteppingPassengersOutcome_IsWhereTheyAre()
    {
        var waiting = Joined();
        var rider = Riding();
        MoveTo(5);

        _registry.Call(waiting, Direction.Up).Floor.ShouldBe(new Floor(1));
        _registry.ChooseFloor(rider, new Floor(9)).Floor.ShouldBe(new Floor(5));
        _registry.Join(rider).Floor.ShouldBe(new Floor(5), "joining again reports where they already are");
    }

    [Fact]
    public void AnUnknownPassenger_IsNeverReportedAsSimulated()
    {
        _registry.Leave(Guid.NewGuid());

        _observer.Events.ShouldHaveSingleItem().Simulated.ShouldBeFalse();
    }

    [Fact]
    public void RemoveSimulated_ReportsRidersWhereTheCarIs_AndRecountsOnce()
    {
        _elevator.OpenDoor();
        var rider = Guid.NewGuid();
        _registry.Join(rider, simulated: true);
        _registry.Board(rider);
        MoveTo(3);
        var countsBefore = _observer.Counts.Count;

        _registry.RemoveSimulated();

        _observer.Events.OfType<PassengerRemoved>().Single().Floor.ShouldBe(new Floor(3));
        _observer.Counts.Count.ShouldBe(countsBefore + 1);
    }

    [Fact]
    public void RemoveSimulated_WithNobodySimulated_DoesNotRecount()
    {
        Joined();
        var countsBefore = _observer.Counts.Count;

        _registry.RemoveSimulated().ShouldBe(0);

        _observer.Counts.Count.ShouldBe(countsBefore);
    }

    [Fact]
    public void Retire_RemovesThePassenger_AndRecounts()
    {
        var id = Guid.NewGuid();
        _registry.Join(id, simulated: true);
        var countsBefore = _observer.Counts.Count;

        _registry.Retire(id);

        _registry.Find(id).ShouldBeNull();
        _observer.Counts.Count.ShouldBe(countsBefore + 1);
        _observer.Counts[^1].Floors[0].Present.ShouldBe(0);
    }

    [Fact]
    public void GoingOutOfService_WithNobodyInside_StillRecounts()
    {
        Joined();
        var countsBefore = _observer.Counts.Count;

        foreach (var elevatorEvent in _elevator.TakeOutOfService(OutOfServiceReason.Fault).Events)
        {
            _registry.Publish(elevatorEvent);
        }

        _observer.Counts.Count.ShouldBe(countsBefore + 1);
    }

    [Fact]
    public void Counts_NameEveryFloor_InOrder()
    {
        _registry.Current.Floors.Select(floor => floor.Floor).ShouldBe([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
    }

    [Fact]
    public void AverageWait_IsTheMeanOfTheWaitsOfThoseWhoCalled()
    {
        var first = Joined();
        var second = Joined();
        _registry.Call(first, Direction.Up);
        _time.Advance(TimeSpan.FromSeconds(4));
        _registry.Call(second, Direction.Up);
        _time.Advance(TimeSpan.FromSeconds(2));
        _elevator.OpenDoor();

        _registry.Board(first);
        _registry.Board(second);

        _registry.Current.AverageWaitTime.ShouldBe(TimeSpan.FromSeconds(4), "(6 s + 2 s) / 2");
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
        if (_elevator.State == ElevatorState.DoorOpen)
        {
            _elevator.CloseDoor();
        }

        while (_elevator.CurrentFloor.Number < floor)
        {
            _elevator.MoveUp();
        }

        while (_elevator.CurrentFloor.Number > floor)
        {
            _elevator.MoveDown();
        }
    }
}
