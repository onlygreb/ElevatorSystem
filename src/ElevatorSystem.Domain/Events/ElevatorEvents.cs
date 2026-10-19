using System.Collections.Immutable;

namespace ElevatorSystem.Domain.Events;

/// <summary>Something that happened to the elevator, and the floor it concerns.</summary>
public abstract record ElevatorEvent(DateTimeOffset OccurredAt, Floor Floor);

/// <summary>A request created a new pending call at <see cref="Position"/> in the queue (1 is next).</summary>
public sealed record CallQueued(DateTimeOffset OccurredAt, Floor Floor, PassengerRequest Request, int Position)
    : ElevatorEvent(OccurredAt, Floor);

/// <summary>A request repeated a call that was already pending and was merged into it.</summary>
public sealed record CallMerged(DateTimeOffset OccurredAt, Floor Floor, PassengerRequest Request, int Position)
    : ElevatorEvent(OccurredAt, Floor);

/// <summary>A submitted request was refused by the elevator, for example because it went out of service meanwhile.</summary>
public sealed record RequestRejected(DateTimeOffset OccurredAt, Floor Floor, PassengerRequest Request, RequestRejectionReason Reason)
    : ElevatorEvent(OccurredAt, Floor);

/// <summary>The car left <see cref="From"/> and is travelling to the adjacent <see cref="ElevatorEvent.Floor"/>.</summary>
public sealed record ElevatorMoved(DateTimeOffset OccurredAt, Floor Floor, Floor From, Direction Direction)
    : ElevatorEvent(OccurredAt, Floor);

/// <summary>The car stopped and opened its door.</summary>
public sealed record DoorsOpened(DateTimeOffset OccurredAt, Floor Floor) : ElevatorEvent(OccurredAt, Floor);

/// <summary>A pending call was served because the door opened at its floor.</summary>
public sealed record CallServed(DateTimeOffset OccurredAt, Floor Floor, PendingCall Call, TimeSpan WaitTime)
    : ElevatorEvent(OccurredAt, Floor);

/// <summary>The door closed and the car is idle at the floor.</summary>
public sealed record DoorsClosed(DateTimeOffset OccurredAt, Floor Floor) : ElevatorEvent(OccurredAt, Floor);

/// <summary>The car was taken out of service; every pending call was cancelled.</summary>
public sealed record ElevatorTakenOutOfService(
    DateTimeOffset OccurredAt,
    Floor Floor,
    OutOfServiceReason Reason,
    ImmutableArray<PendingCall> CancelledCalls) : ElevatorEvent(OccurredAt, Floor);

/// <summary>The car is back in service, idle with the door closed.</summary>
public sealed record ElevatorReturnedToService(DateTimeOffset OccurredAt, Floor Floor) : ElevatorEvent(OccurredAt, Floor);
