namespace ElevatorSystem.Contracts;

// Wire contracts, version 1. Additive changes only: a breaking change means new types and routes (ADR-0002).
// Enums travel as their names ("Up", "MovingUp"), never as numbers.

/// <summary>Body of <c>POST /api/v1/hall-calls</c>: someone on <see cref="Floor"/> wants to go <see cref="Direction"/>.</summary>
public sealed record HallCallRequest(int Floor, CallDirection Direction);

/// <summary>Body of <c>POST /api/v1/car-calls</c>: a passenger inside the car chose <see cref="Floor"/>.</summary>
public sealed record CarCallRequest(int Floor);

/// <summary>The request was accepted; its progress is reported through the hub's activity feed.</summary>
public sealed record RequestAcceptedResponse(Guid RequestId);

/// <summary>Building and timing settings, so a client can draw the shaft and animate at the right speed.</summary>
public sealed record ElevatorConfigurationDto(
    int LowestFloor,
    int HighestFloor,
    TimeSpan TravelTimePerFloor,
    TimeSpan DoorOpenDuration,
    string SchedulingStrategy);

/// <summary>A consistent view of the elevator at one moment. Higher <see cref="Version"/> is newer.</summary>
/// <param name="CurrentFloor">The floor the car is at, or travelling to while moving.</param>
/// <param name="Status">What the car is doing.</param>
/// <param name="TravelDirection">Direction of the current or most recent trip; null before the first one.</param>
/// <param name="OutOfServiceCause">Why the car is out of service, when it is.</param>
/// <param name="PendingCalls">Calls still to be served, oldest first.</param>
/// <param name="TargetFloors">Floors still to visit, in the order they were first requested.</param>
/// <param name="Version">Increases with every change; ignore a status older than the one shown.</param>
/// <param name="TakenAt">When the status was taken.</param>
public sealed record ElevatorStatusDto(
    int CurrentFloor,
    ElevatorStatus Status,
    CallDirection? TravelDirection,
    OutOfServiceCause? OutOfServiceCause,
    IReadOnlyList<PendingCallDto> PendingCalls,
    IReadOnlyList<int> TargetFloors,
    long Version,
    DateTimeOffset TakenAt);

/// <summary>A call: a hall call has a <see cref="Direction"/>, a car call does not.</summary>
public sealed record CallDto(CallKind Kind, int Floor, CallDirection? Direction);

/// <summary>A call waiting to be served and how many requests were merged into it.</summary>
public sealed record PendingCallDto(CallDto Call, int RequestCount, DateTimeOffset FirstRequestedAt);

/// <summary>
/// One entry of the activity feed. <see cref="Kind"/> says which optional fields are set:
/// <list type="bullet">
/// <item><c>CallQueued</c>, <c>CallMerged</c>: <see cref="Call"/>, <see cref="RequestId"/>, <see cref="Position"/>.</item>
/// <item><c>RequestRejected</c>: <see cref="Call"/>, <see cref="RequestId"/>, <see cref="RejectionReason"/>.</item>
/// <item><c>Moved</c>: <see cref="FromFloor"/> and <see cref="Direction"/>; <see cref="Floor"/> is the destination.</item>
/// <item><c>CallServed</c>: <see cref="Call"/>, <see cref="RequestId"/> (the first request), <see cref="RequestCount"/>, <see cref="WaitTime"/>.</item>
/// <item><c>OutOfService</c>: <see cref="OutOfServiceCause"/>, <see cref="CancelledCalls"/>.</item>
/// <item><c>DoorsOpened</c>, <c>DoorsClosed</c>, <c>ReturnedToService</c>: <see cref="Floor"/> only.</item>
/// </list>
/// </summary>
public sealed record ElevatorActivityDto(
    DateTimeOffset OccurredAt,
    ElevatorActivityKind Kind,
    int Floor,
    CallDto? Call = null,
    Guid? RequestId = null,
    int? Position = null,
    int? FromFloor = null,
    CallDirection? Direction = null,
    int? RequestCount = null,
    TimeSpan? WaitTime = null,
    RejectionReason? RejectionReason = null,
    OutOfServiceCause? OutOfServiceCause = null,
    int? CancelledCalls = null);

/// <summary>
/// How many passengers are where, counted from the calls they place (ADR-0017). Counts only:
/// passengers are anonymous by design and nothing identifies an individual (ADR-0009).
/// </summary>
/// <param name="PassengersInCar">People riding the elevator now.</param>
/// <param name="CarCapacity">The most people the car carries at once.</param>
/// <param name="Floors">One entry per floor, lowest first.</param>
/// <param name="PassengersDelivered">Trips completed since the engine started.</param>
/// <param name="AverageWaitTime">Mean time from calling the elevator to boarding, over completed boardings; null before the first.</param>
/// <param name="TakenAt">When the counts were taken.</param>
public sealed record BuildingOccupancyDto(
    int PassengersInCar,
    int CarCapacity,
    IReadOnlyList<FloorOccupancyDto> Floors,
    int PassengersDelivered,
    TimeSpan? AverageWaitTime,
    DateTimeOffset TakenAt);

/// <summary>People on one floor: <see cref="Waiting"/> for the elevator, and <see cref="Present"/> on the floor but not waiting.</summary>
public sealed record FloorOccupancyDto(int Floor, int Waiting, int Present);

/// <summary>
/// What the hub sends to every connected client. A client receives <see cref="StatusChanged"/> right after
/// connecting and after every change; <see cref="ActivityOccurred"/> narrates each step in order.
/// </summary>
public interface IElevatorClient
{
    Task StatusChanged(ElevatorStatusDto status);

    Task ActivityOccurred(ElevatorActivityDto activity);

    /// <summary>Sent on connect and whenever the passenger counts change (ADR-0017).</summary>
    Task OccupancyChanged(BuildingOccupancyDto occupancy);
}

public enum CallDirection
{
    Up,
    Down,
}

public enum CallKind
{
    Hall,
    Car,
}

public enum ElevatorStatus
{
    Idle,
    MovingUp,
    MovingDown,
    DoorOpen,
    OutOfService,
}

public enum OutOfServiceCause
{
    Maintenance,
    Fault,
    EmergencyStop,
}

/// <summary>Why a request was refused; carried in the ProblemDetails <c>reason</c> extension and the activity feed.</summary>
public enum RejectionReason
{
    InvalidFloor,
    InvalidDirection,
    OutOfService,
    Busy,
}

public enum ElevatorActivityKind
{
    CallQueued,
    CallMerged,
    RequestRejected,
    Moved,
    DoorsOpened,
    CallServed,
    DoorsClosed,
    OutOfService,
    ReturnedToService,
}
