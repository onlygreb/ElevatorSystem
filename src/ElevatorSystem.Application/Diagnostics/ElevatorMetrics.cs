using System.Diagnostics.Metrics;
using ElevatorSystem.Application.Passengers;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;

namespace ElevatorSystem.Application.Diagnostics;

/// <summary>
/// The elevator's OpenTelemetry metrics (ADR-0028), on the <see cref="MeterName"/> meter: requests by outcome, call and
/// passenger wait times, floors travelled, door openings, service interruptions and passenger steps, plus gauges for the
/// car's floor and how many people are where. It listens to the same events as everything else and never blocks.
/// </summary>
/// <remarks>
/// Tags are small closed sets (call kind, outcome, reason, direction, step): never a passenger or request id, so the
/// metrics carry nothing about who rides the elevator. The gauges report only while this instance drives the car; a
/// standby reports nothing rather than a stale floor.
/// </remarks>
public sealed class ElevatorMetrics : IElevatorEventSink, IPassengerObserver
{
    public const string MeterName = "ElevatorSystem.Elevator";

    private static readonly InstrumentAdvice<double> WaitBuckets = new()
    {
        HistogramBucketBoundaries = [0.5, 1, 2, 5, 10, 20, 30, 60, 120, 300],
    };

    private readonly Counter<long> _requests;
    private readonly Histogram<double> _callWaitTime;
    private readonly Counter<long> _floorsTravelled;
    private readonly Counter<long> _doorOpenings;
    private readonly Counter<long> _outOfService;
    private readonly Counter<long> _cancelledCalls;
    private readonly Counter<long> _passengerSteps;
    private readonly Histogram<double> _passengerWaitTime;
    private volatile bool _hasFloor;
    private volatile int _floor;
    private volatile BuildingOccupancyDto? _occupancy;

    public ElevatorMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);

        _requests = meter.CreateCounter<long>("elevator.requests", "{request}", "Requests taken in, by call kind and outcome.");
        _callWaitTime = meter.CreateHistogram("elevator.call.wait_time", "s", "Time from a call's first request until the doors opened for it.", advice: WaitBuckets);
        _floorsTravelled = meter.CreateCounter<long>("elevator.floors_travelled", "{floor}", "Floors the car moved, by direction.");
        _doorOpenings = meter.CreateCounter<long>("elevator.door.openings", "{opening}", "Times the car stopped and opened its doors.");
        _outOfService = meter.CreateCounter<long>("elevator.out_of_service", "{interruption}", "Times the car was taken out of service, by reason.");
        _cancelledCalls = meter.CreateCounter<long>("elevator.calls.cancelled", "{call}", "Pending calls cancelled when the car went out of service.");
        _passengerSteps = meter.CreateCounter<long>("elevator.passenger.steps", "{step}", "Passenger steps, by step and outcome.");
        _passengerWaitTime = meter.CreateHistogram("elevator.passenger.wait_time", "s", "Time from a passenger's call until they walked into the car.", advice: WaitBuckets);

        meter.CreateObservableGauge("elevator.floor", ObserveFloor, "{floor}", "The floor the car is at.");
        meter.CreateObservableGauge("elevator.passengers", ObservePassengers, "{passenger}", "People in the building, by where they are.");
    }

    public void Publish(ElevatorEvent elevatorEvent)
    {
        ArgumentNullException.ThrowIfNull(elevatorEvent);
        _floor = elevatorEvent.Floor.Number;
        _hasFloor = true;

        switch (elevatorEvent)
        {
            case CallQueued queued:
                _requests.Add(1, KindTag(queued.Request.Call), new("elevator.request.outcome", "queued"));
                break;
            case CallMerged merged:
                _requests.Add(1, KindTag(merged.Request.Call), new("elevator.request.outcome", "merged"));
                break;
            case RequestRejected rejected:
                _requests.Add(
                    1,
                    KindTag(rejected.Request.Call),
                    new("elevator.request.outcome", "rejected"),
                    new("elevator.request.rejection_reason", rejected.Reason.ToString()));
                break;
            case ElevatorMoved moved:
                _floorsTravelled.Add(1, new KeyValuePair<string, object?>("elevator.direction", moved.Direction == Direction.Up ? "up" : "down"));
                break;
            case DoorsOpened:
                _doorOpenings.Add(1);
                break;
            case CallServed served:
                _callWaitTime.Record(served.WaitTime.TotalSeconds, KindTag(served.Call.Call));
                break;
            case ElevatorTakenOutOfService outOfService:
                _outOfService.Add(1, new KeyValuePair<string, object?>("elevator.out_of_service.reason", outOfService.Reason.ToString()));
                if (outOfService.CancelledCalls.Length > 0)
                {
                    _cancelledCalls.Add(outOfService.CancelledCalls.Length);
                }

                break;
        }
    }

    public void PassengerChanged(PassengerEvent passengerEvent)
    {
        ArgumentNullException.ThrowIfNull(passengerEvent);
        var simulated = new KeyValuePair<string, object?>("elevator.passenger.simulated", passengerEvent.Simulated);

        if (passengerEvent is PassengerRefused refused)
        {
            _passengerSteps.Add(
                1,
                new("elevator.passenger.step", StepName(refused.Action)),
                new("elevator.passenger.outcome", "refused"),
                new("elevator.passenger.refusal_reason", refused.Reason.ToString()),
                simulated);
            return;
        }

        _passengerSteps.Add(1, new("elevator.passenger.step", StepName(passengerEvent)), new("elevator.passenger.outcome", "done"), simulated);
        if (passengerEvent is PassengerBoarded { WaitTime: { } wait })
        {
            _passengerWaitTime.Record(wait.TotalSeconds, simulated);
        }
    }

    public void OccupancyChanged(BuildingOccupancyDto occupancy)
    {
        ArgumentNullException.ThrowIfNull(occupancy);
        _occupancy = occupancy;
    }

    /// <summary>Forgets the car's state when this instance stops driving it, so the gauges go quiet.</summary>
    public void Forget()
    {
        _hasFloor = false;
        _occupancy = null;
    }

    private IEnumerable<Measurement<int>> ObserveFloor() =>
        _hasFloor ? [new Measurement<int>(_floor)] : [];

    private IEnumerable<Measurement<int>> ObservePassengers()
    {
        if (_occupancy is not { } occupancy)
        {
            return [];
        }

        return
        [
            new Measurement<int>(occupancy.PassengersInCar, new KeyValuePair<string, object?>("elevator.passenger.location", "in_car")),
            new Measurement<int>(occupancy.Floors.Sum(floor => floor.Waiting), new KeyValuePair<string, object?>("elevator.passenger.location", "waiting")),
            new Measurement<int>(occupancy.Floors.Sum(floor => floor.Present), new KeyValuePair<string, object?>("elevator.passenger.location", "on_floor")),
        ];
    }

    private static KeyValuePair<string, object?> KindTag(ElevatorCall call) =>
        new("elevator.call.kind", call is HallCall ? "hall" : "car");

    private static string StepName(PassengerEvent passengerEvent) => passengerEvent switch
    {
        PassengerJoined => "join",
        PassengerCalled => "call",
        PassengerBoarded => "board",
        PassengerChoseFloor => "choose_floor",
        PassengerLeft => "leave",
        _ => "remove",
    };

    private static string StepName(PassengerAction action) => action switch
    {
        PassengerAction.Join => "join",
        PassengerAction.CallElevator => "call",
        PassengerAction.Board => "board",
        PassengerAction.ChooseFloor => "choose_floor",
        PassengerAction.Leave => "leave",
        _ => "unknown",
    };
}
