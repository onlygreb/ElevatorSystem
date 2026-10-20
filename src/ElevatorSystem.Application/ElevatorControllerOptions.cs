namespace ElevatorSystem.Application;

/// <summary>Timing and capacity settings for <see cref="ElevatorController"/>.</summary>
public sealed record ElevatorControllerOptions
{
    /// <summary>How long the car normally takes to travel one floor.</summary>
    public TimeSpan TravelTimePerFloor { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>How long the door stays open at a stop when the car has somewhere to go next.</summary>
    public TimeSpan DoorOpenDuration { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>
    /// With nothing to do, the car waits with its door open (<see langword="true"/>, the default) so passengers can
    /// walk in, and the <see cref="DoorOpenDuration"/> countdown starts only when a call gives it somewhere to go.
    /// <see langword="false"/> closes the door after every stop and waits closed (ADR-0025).
    /// </summary>
    public bool HoldDoorOpenWhenIdle { get; init; } = true;

    /// <summary>
    /// How many times the normal travel time a trip may take before the car is considered stuck
    /// and taken out of service.
    /// </summary>
    public double StuckTolerance { get; init; } = 3;

    /// <summary>How many submitted requests may wait for the control loop before new ones are refused as busy.</summary>
    public int IntakeCapacity { get; init; } = 1024;

    /// <summary>The watchdog limit for one trip: <see cref="TravelTimePerFloor"/> × <see cref="StuckTolerance"/>.</summary>
    public TimeSpan StuckTimeout => TravelTimePerFloor * StuckTolerance;

    /// <summary>Throws if any setting is out of range.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(TravelTimePerFloor, TimeSpan.Zero, nameof(TravelTimePerFloor));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(DoorOpenDuration, TimeSpan.Zero, nameof(DoorOpenDuration));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(StuckTolerance, 1.0, nameof(StuckTolerance));
        ArgumentOutOfRangeException.ThrowIfLessThan(IntakeCapacity, 1, nameof(IntakeCapacity));
    }
}
