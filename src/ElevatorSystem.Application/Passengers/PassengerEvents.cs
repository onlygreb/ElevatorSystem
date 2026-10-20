using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;

namespace ElevatorSystem.Application.Passengers;

/// <summary>Something a passenger did, or could not do (ADR-0026). Published in order, after the registry changed.</summary>
/// <param name="OccurredAt">When it happened.</param>
/// <param name="PassengerId">The anonymous passenger.</param>
/// <param name="Floor">Where it happened: the passenger's floor, or the car's while they ride.</param>
/// <param name="Simulated">A passenger of a demo scenario.</param>
/// <param name="RequestId">The step that caused it, so the caller can follow its own request.</param>
public abstract record PassengerEvent(DateTimeOffset OccurredAt, Guid PassengerId, Floor Floor, bool Simulated, Guid? RequestId);

/// <summary>A passenger arrived in the building, on the lowest floor.</summary>
public sealed record PassengerJoined(DateTimeOffset OccurredAt, Guid PassengerId, Floor Floor, bool Simulated, Guid? RequestId)
    : PassengerEvent(OccurredAt, PassengerId, Floor, Simulated, RequestId);

/// <summary>A passenger pressed the button on their floor and now waits for the car.</summary>
public sealed record PassengerCalled(DateTimeOffset OccurredAt, Guid PassengerId, Floor Floor, bool Simulated, Guid? RequestId, Direction Direction)
    : PassengerEvent(OccurredAt, PassengerId, Floor, Simulated, RequestId);

/// <summary>A passenger walked into the car; <see cref="WaitTime"/> runs from their call, when they had called.</summary>
public sealed record PassengerBoarded(DateTimeOffset OccurredAt, Guid PassengerId, Floor Floor, bool Simulated, Guid? RequestId, TimeSpan? WaitTime)
    : PassengerEvent(OccurredAt, PassengerId, Floor, Simulated, RequestId);

/// <summary>A passenger inside the car chose <see cref="Destination"/>.</summary>
public sealed record PassengerChoseFloor(DateTimeOffset OccurredAt, Guid PassengerId, Floor Floor, bool Simulated, Guid? RequestId, Floor Destination)
    : PassengerEvent(OccurredAt, PassengerId, Floor, Simulated, RequestId);

/// <summary>A passenger walked out of the car onto <see cref="PassengerEvent.Floor"/>.</summary>
public sealed record PassengerLeft(DateTimeOffset OccurredAt, Guid PassengerId, Floor Floor, bool Simulated, Guid? RequestId)
    : PassengerEvent(OccurredAt, PassengerId, Floor, Simulated, RequestId);

/// <summary>A passenger left the building: inactive for too long, or a demo scenario ended.</summary>
public sealed record PassengerRemoved(DateTimeOffset OccurredAt, Guid PassengerId, Floor Floor, bool Simulated, Guid? RequestId)
    : PassengerEvent(OccurredAt, PassengerId, Floor, Simulated, RequestId);

/// <summary>A passenger's step was refused; nothing changed.</summary>
public sealed record PassengerRefused(
    DateTimeOffset OccurredAt, Guid PassengerId, Floor Floor, bool Simulated, Guid? RequestId, PassengerAction Action, PassengerRefusalReason Reason)
    : PassengerEvent(OccurredAt, PassengerId, Floor, Simulated, RequestId);

/// <summary>Told about every passenger event and every change of the counts, in order, from under the registry's lock.</summary>
/// <remarks>Implementations must be thread-safe and fast: hand the data to a buffer rather than doing I/O.</remarks>
public interface IPassengerObserver
{
    void PassengerChanged(PassengerEvent passengerEvent);

    void OccupancyChanged(BuildingOccupancyDto occupancy);
}

/// <summary>Turns passenger events into activity-feed entries.</summary>
public static class PassengerActivity
{
    public static ElevatorActivityDto ToActivity(PassengerEvent passengerEvent)
    {
        ArgumentNullException.ThrowIfNull(passengerEvent);

        var at = passengerEvent.OccurredAt;
        var floor = passengerEvent.Floor.Number;
        var id = passengerEvent.PassengerId;
        var simulated = passengerEvent.Simulated;
        var request = passengerEvent.RequestId;
        return passengerEvent switch
        {
            PassengerJoined => new(at, ElevatorActivityKind.PassengerJoined, floor, RequestId: request, PassengerId: id, Simulated: simulated),
            PassengerCalled called => new(at, ElevatorActivityKind.PassengerCalled, floor,
                RequestId: request, Direction: ElevatorContractMapper.ToContract(called.Direction), PassengerId: id, Simulated: simulated),
            PassengerBoarded boarded => new(at, ElevatorActivityKind.PassengerBoarded, floor,
                RequestId: request, WaitTime: boarded.WaitTime, PassengerId: id, Simulated: simulated),
            PassengerChoseFloor chose => new(at, ElevatorActivityKind.PassengerChoseFloor, chose.Destination.Number,
                RequestId: request, FromFloor: floor, PassengerId: id, Simulated: simulated),
            PassengerLeft => new(at, ElevatorActivityKind.PassengerLeft, floor, RequestId: request, PassengerId: id, Simulated: simulated),
            PassengerRemoved => new(at, ElevatorActivityKind.PassengerRemoved, floor, RequestId: request, PassengerId: id, Simulated: simulated),
            PassengerRefused refused => new(at, ElevatorActivityKind.PassengerRefused, floor,
                RequestId: request, PassengerId: id, Simulated: simulated, PassengerAction: refused.Action, PassengerRefusal: refused.Reason),
            _ => throw new ArgumentOutOfRangeException(nameof(passengerEvent), passengerEvent.GetType().Name, "Unmapped passenger event."),
        };
    }
}
