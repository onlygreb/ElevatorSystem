using System.Collections.Immutable;

namespace ElevatorSystem.Domain;

/// <summary>A consistent, immutable view of the elevator at one moment.</summary>
/// <param name="CurrentFloor">The floor the car is at, or travelling to while it is moving.</param>
/// <param name="State">What the car is doing.</param>
/// <param name="TravelDirection">Direction of the current or most recent trip; <see langword="null"/> before the first one.</param>
/// <param name="OutOfServiceReason">Why the car is out of service, when it is.</param>
/// <param name="PendingCalls">Calls still to be served, in the order they were first requested.</param>
/// <param name="Version">Increases with every change, so observers can order snapshots.</param>
/// <param name="TakenAt">When the snapshot was taken.</param>
public sealed record ElevatorSnapshot(
    Floor CurrentFloor,
    ElevatorState State,
    Direction? TravelDirection,
    OutOfServiceReason? OutOfServiceReason,
    ImmutableArray<PendingCall> PendingCalls,
    long Version,
    DateTimeOffset TakenAt)
{
    /// <summary>Floors the car still has to stop at, without duplicates, in the order they were first requested.</summary>
    public ImmutableArray<Floor> TargetFloors => [.. PendingCalls.Select(pending => pending.Call.Floor).Distinct()];
}
