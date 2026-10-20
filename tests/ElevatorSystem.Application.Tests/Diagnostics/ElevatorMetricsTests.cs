using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using ElevatorSystem.Application.Diagnostics;
using ElevatorSystem.Application.Passengers;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace ElevatorSystem.Application.Tests.Diagnostics;

public sealed class ElevatorMetricsTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly TestMeterFactory _meters = new();
    private readonly ElevatorMetrics _metrics;

    public ElevatorMetricsTests() => _metrics = new ElevatorMetrics(_meters);

    public void Dispose() => _meters.Dispose();

    [Fact]
    public void Requests_AreCountedByCallKindAndOutcome()
    {
        using var requests = Collect<long>("elevator.requests");

        _metrics.Publish(new CallQueued(At, new Floor(3), Request(Hall(3)), 1));
        _metrics.Publish(new CallMerged(At, new Floor(3), Request(Hall(3)), 1));
        _metrics.Publish(new RequestRejected(At, new Floor(12), Request(new CarCall(new Floor(12))), RequestRejectionReason.InvalidFloor));

        var measured = requests.GetMeasurementSnapshot();
        measured.Select(m => (m.Tags["elevator.call.kind"], m.Tags["elevator.request.outcome"]))
            .ShouldBe([("hall", "queued"), ("hall", "merged"), ("car", "rejected")]);
        measured[2].Tags["elevator.request.rejection_reason"].ShouldBe("InvalidFloor");
        measured.ShouldAllBe(m => m.Value == 1);
    }

    [Fact]
    public void ServedCall_RecordsItsWaitInSeconds()
    {
        using var wait = Collect<double>("elevator.call.wait_time");

        _metrics.Publish(new CallServed(At, new Floor(5), new PendingCall(Hall(5), RequestId(), At, 2), TimeSpan.FromSeconds(7.5)));

        var measured = wait.LastMeasurement.ShouldNotBeNull();
        measured.Value.ShouldBe(7.5);
        measured.Tags["elevator.call.kind"].ShouldBe("hall");
    }

    [Fact]
    public void Travel_AndDoorOpenings_AreCounted()
    {
        using var floors = Collect<long>("elevator.floors_travelled");
        using var doors = Collect<long>("elevator.door.openings");

        _metrics.Publish(new ElevatorMoved(At, new Floor(2), new Floor(1), Direction.Up));
        _metrics.Publish(new ElevatorMoved(At, new Floor(1), new Floor(2), Direction.Down));
        _metrics.Publish(new DoorsOpened(At, new Floor(1)));

        floors.GetMeasurementSnapshot().Select(m => m.Tags["elevator.direction"]).ShouldBe(["up", "down"]);
        doors.GetMeasurementSnapshot().Count.ShouldBe(1);
    }

    [Fact]
    public void OutOfService_CountsTheReasonAndTheCancelledCalls()
    {
        using var interruptions = Collect<long>("elevator.out_of_service");
        using var cancelled = Collect<long>("elevator.calls.cancelled");
        ImmutableArray<PendingCall> calls = [new(Hall(4), RequestId(), At, 1), new(new CarCall(new Floor(9)), RequestId(), At, 3)];

        _metrics.Publish(new ElevatorTakenOutOfService(At, new Floor(4), OutOfServiceReason.EmergencyStop, calls));
        _metrics.Publish(new ElevatorTakenOutOfService(At, new Floor(4), OutOfServiceReason.Maintenance, []));

        interruptions.GetMeasurementSnapshot().Select(m => m.Tags["elevator.out_of_service.reason"]).ShouldBe(["EmergencyStop", "Maintenance"]);
        cancelled.GetMeasurementSnapshot().Select(m => m.Value).ShouldBe([2L], "an interruption with nothing pending cancels nothing");
    }

    [Fact]
    public void ClearingTheQueue_CountsTheCancelledCalls()
    {
        using var cancelled = Collect<long>("elevator.calls.cancelled");
        ImmutableArray<PendingCall> calls = [new(Hall(4), RequestId(), At, 1), new(new CarCall(new Floor(9)), RequestId(), At, 3), new(Hall(7), RequestId(), At, 2)];

        _metrics.Publish(new RequestsCleared(At, new Floor(2), calls));

        cancelled.GetMeasurementSnapshot().Select(m => m.Value).ShouldBe([3L]);
    }

    [Fact]
    public void PassengerSteps_AreCountedByStepAndOutcome()
    {
        using var steps = Collect<long>("elevator.passenger.steps");
        var id = Guid.NewGuid();

        _metrics.PassengerChanged(new PassengerJoined(At, id, new Floor(1), false, null));
        _metrics.PassengerChanged(new PassengerCalled(At, id, new Floor(1), false, null, Direction.Up));
        _metrics.PassengerChanged(new PassengerBoarded(At, id, new Floor(1), false, null, TimeSpan.FromSeconds(3)));
        _metrics.PassengerChanged(new PassengerChoseFloor(At, id, new Floor(1), false, null, new Floor(6)));
        _metrics.PassengerChanged(new PassengerLeft(At, id, new Floor(6), false, null));
        _metrics.PassengerChanged(new PassengerRemoved(At, id, new Floor(6), true, null));
        _metrics.PassengerChanged(new PassengerRefused(At, id, new Floor(6), true, null, PassengerAction.Board, PassengerRefusalReason.CarFull));

        var measured = steps.GetMeasurementSnapshot();
        measured.Select(m => (m.Tags["elevator.passenger.step"], m.Tags["elevator.passenger.outcome"])).ShouldBe(
        [
            ("join", "done"), ("call", "done"), ("board", "done"), ("choose_floor", "done"), ("leave", "done"), ("remove", "done"), ("board", "refused"),
        ]);
        measured[^1].Tags["elevator.passenger.refusal_reason"].ShouldBe("CarFull");
        measured.Select(m => m.Tags["elevator.passenger.simulated"]).ShouldBe([false, false, false, false, false, true, true]);
    }

    [Fact]
    public void Boarding_AfterACall_RecordsThePassengersWait()
    {
        using var wait = Collect<double>("elevator.passenger.wait_time");

        _metrics.PassengerChanged(new PassengerBoarded(At, Guid.NewGuid(), new Floor(1), false, null, WaitTime: null));
        _metrics.PassengerChanged(new PassengerBoarded(At, Guid.NewGuid(), new Floor(1), true, null, TimeSpan.FromSeconds(12)));

        var measured = wait.GetMeasurementSnapshot().ShouldHaveSingleItem("walking straight into an open car is no wait to measure");
        measured.Value.ShouldBe(12);
        measured.Tags["elevator.passenger.simulated"].ShouldBe(true);
    }

    [Fact]
    public void Gauges_ReportTheCarAndThePeople_OnlyWhileTheyAreKnown()
    {
        using var floor = Collect<int>("elevator.floor");
        using var people = Collect<int>("elevator.passengers");

        floor.RecordObservableInstruments();
        floor.GetMeasurementSnapshot().ShouldBeEmpty("nothing is known before the first event");

        _metrics.Publish(new ElevatorMoved(At, new Floor(4), new Floor(3), Direction.Up));
        _metrics.OccupancyChanged(new BuildingOccupancyDto(2, 8, [new(1, 3, 1), new(4, 1, 5)], 0, null, At));
        floor.RecordObservableInstruments();
        people.RecordObservableInstruments();

        floor.LastMeasurement.ShouldNotBeNull().Value.ShouldBe(4);
        people.GetMeasurementSnapshot().ToDictionary(m => (string)m.Tags["elevator.passenger.location"]!, m => m.Value)
            .ShouldBe(new Dictionary<string, int> { ["in_car"] = 2, ["waiting"] = 4, ["on_floor"] = 6 });

        _metrics.Forget();
        floor.Clear();
        people.Clear();
        floor.RecordObservableInstruments();
        people.RecordObservableInstruments();
        floor.GetMeasurementSnapshot().ShouldBeEmpty("a standby reports no stale floor");
        people.GetMeasurementSnapshot().ShouldBeEmpty();
    }

    [Fact]
    public void Tags_NeverCarryAnIdentifier()
    {
        using var all = new MeterListener();
        var tagValues = new List<object?>();
        all.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Scope == _meters)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        all.SetMeasurementEventCallback<long>((_, _, tags, _) => tagValues.AddRange(tags.ToArray().Select(tag => tag.Value)));
        all.SetMeasurementEventCallback<double>((_, _, tags, _) => tagValues.AddRange(tags.ToArray().Select(tag => tag.Value)));
        all.Start();
        var passenger = Guid.NewGuid();
        var request = Request(Hall(2));

        _metrics.Publish(new CallQueued(At, new Floor(2), request, 1));
        _metrics.Publish(new CallServed(At, new Floor(2), new PendingCall(request.Call, request.Id, At, 1), TimeSpan.FromSeconds(1)));
        _metrics.PassengerChanged(new PassengerBoarded(At, passenger, new Floor(2), false, request.Id.Value, TimeSpan.FromSeconds(1)));
        _metrics.PassengerChanged(new PassengerRefused(At, passenger, new Floor(2), false, request.Id.Value, PassengerAction.Leave, PassengerRefusalReason.DoorClosed));

        tagValues.ShouldNotBeEmpty();
        tagValues.ShouldAllBe(value => IsAClosedSetValue(value));
        tagValues.ShouldNotContain(passenger.ToString());
        tagValues.ShouldNotContain(request.Id.Value.ToString());
    }

    private static bool IsAClosedSetValue(object? value) => value is bool || (value is string text && !Guid.TryParse(text, out _));

    private MetricCollector<T> Collect<T>(string instrument) where T : struct =>
        new(_meters, ElevatorMetrics.MeterName, instrument);

    private static HallCall Hall(int floor) => new(new Floor(floor), Direction.Up);

    private static RequestId RequestId() => new(Guid.NewGuid());

    private static PassengerRequest Request(ElevatorCall call) => new(RequestId(), call, At);
}
