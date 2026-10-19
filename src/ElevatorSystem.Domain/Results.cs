using System.Collections.Immutable;
using ElevatorSystem.Domain.Events;

namespace ElevatorSystem.Domain;

/// <summary>Why a state transition was refused.</summary>
public enum TransitionError
{
    /// <summary>The car cannot move while its door is open.</summary>
    DoorIsOpen,

    /// <summary>The car is already at the top floor.</summary>
    AtTopFloor,

    /// <summary>The car is already at the bottom floor.</summary>
    AtBottomFloor,

    /// <summary>The car must stop before reversing direction.</summary>
    MovingInOppositeDirection,

    /// <summary>The car is out of service.</summary>
    OutOfService,

    /// <summary>The car is already in service.</summary>
    NotOutOfService,
}

/// <summary>The outcome of a state transition and the events it produced.</summary>
/// <param name="Error">Why the transition was refused, or <see langword="null"/> when it succeeded.</param>
/// <param name="Events">What happened; empty when the transition was refused or changed nothing.</param>
public sealed record TransitionResult(TransitionError? Error, ImmutableArray<ElevatorEvent> Events)
{
    /// <summary>A successful transition that changed nothing, such as closing a door that is already closed.</summary>
    public static TransitionResult Unchanged { get; } = new(null, []);

    public bool Succeeded => Error is null;

    internal static TransitionResult Success(params ReadOnlySpan<ElevatorEvent> events) => new(null, [.. events]);

    internal static TransitionResult Rejected(TransitionError error) => new(error, []);
}

/// <summary>What happened to a request handed to the elevator.</summary>
public enum AddRequestOutcome
{
    /// <summary>A new pending call was queued.</summary>
    Queued,

    /// <summary>The same call was already pending; the request was merged into it.</summary>
    Merged,

    /// <summary>The door was already open at that floor, so the call was served at once.</summary>
    ServedImmediately,

    /// <summary>The request was refused; see <see cref="AddRequestResult.RejectionReason"/>.</summary>
    Rejected,
}

/// <summary>The outcome of <see cref="Elevator.AddRequest"/> and the events it produced.</summary>
public sealed record AddRequestResult(
    AddRequestOutcome Outcome,
    RequestRejectionReason? RejectionReason,
    ImmutableArray<ElevatorEvent> Events);
