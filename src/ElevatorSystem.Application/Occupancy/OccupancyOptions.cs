namespace ElevatorSystem.Application.Occupancy;

/// <summary>Settings for <see cref="OccupancyTracker"/> (ADR-0017).</summary>
public sealed record OccupancyOptions
{
    /// <summary>The most people the car carries at once; anyone beyond it is left behind.</summary>
    public int CarCapacity { get; init; } = 8;

    /// <summary>Throws if any setting is out of range.</summary>
    public void Validate() => ArgumentOutOfRangeException.ThrowIfLessThan(CarCapacity, 1, nameof(CarCapacity));
}
