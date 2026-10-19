using System.Globalization;

namespace ElevatorSystem.Domain;

/// <summary>
/// What a passenger asked for: the part of a request that decides where the car must stop.
/// Equal calls are the same stop, so a repeated call is merged instead of queued twice.
/// </summary>
public abstract record ElevatorCall(Floor Floor);

/// <summary>A pickup request from a landing: "I am on this floor and want to go this way".</summary>
public sealed record HallCall(Floor Floor, Direction Direction) : ElevatorCall(Floor)
{
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"hall call at floor {Floor} going {(Direction == Direction.Up ? "up" : "down")}");
}

/// <summary>A destination request from inside the car.</summary>
public sealed record CarCall(Floor Floor) : ElevatorCall(Floor)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"car call to floor {Floor}");
}

/// <summary>Identifies one passenger request. Version 7 GUIDs sort by creation time.</summary>
public readonly record struct RequestId(Guid Value)
{
    public static RequestId New(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        return new RequestId(Guid.CreateVersion7(timeProvider.GetUtcNow()));
    }

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

/// <summary>One request as submitted: its identity, what was asked for, and when.</summary>
public sealed record PassengerRequest(RequestId Id, ElevatorCall Call, DateTimeOffset RequestedAt);

/// <summary>
/// A call waiting to be served. Repeated requests for the same call are merged into it and only
/// counted, so memory stays bounded however many times a button is pressed.
/// </summary>
public sealed record PendingCall(ElevatorCall Call, RequestId FirstRequestId, DateTimeOffset FirstRequestedAt, int RequestCount);

/// <summary>Why a request was refused.</summary>
public enum RequestRejectionReason
{
    /// <summary>The floor is not served by this elevator.</summary>
    InvalidFloor,

    /// <summary>The direction is unknown or impossible from that floor (up from the top, down from the bottom).</summary>
    InvalidDirection,

    /// <summary>The elevator is out of service.</summary>
    OutOfService,

    /// <summary>The request intake is full; the caller may retry shortly.</summary>
    Busy,
}
