namespace ElevatorSystem.Application.Passengers;

/// <summary>Settings of the passengers (ADR-0026), bound from the <c>Passengers</c> configuration section.</summary>
public sealed record PassengerOptions
{
    public const string SectionName = "Passengers";

    /// <summary>The most people the car carries at once; anyone else has to wait for the next trip.</summary>
    public int CarCapacity { get; init; } = 8;

    /// <summary>The most passengers in the building at once, people and simulated ones together.</summary>
    public int MaxPassengers { get; init; } = 300;

    /// <summary>A person who takes no step for this long has left the building (they closed the page).</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Throws if any setting is out of range.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(CarCapacity, 1, nameof(CarCapacity));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPassengers, 1, nameof(MaxPassengers));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(IdleTimeout, TimeSpan.Zero, nameof(IdleTimeout));
    }
}
