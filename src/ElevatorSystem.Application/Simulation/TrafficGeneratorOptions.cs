namespace ElevatorSystem.Application.Simulation;

/// <summary>
/// Settings for <see cref="TrafficGenerator"/>, a test and load tool (ADR-0017 §4). Off by default and
/// never enabled in production configuration.
/// </summary>
public sealed record TrafficGeneratorOptions
{
    /// <summary>Whether the generator places calls at all. Off unless a test, a load run or the demo turns it on.</summary>
    public bool Enabled { get; init; }

    /// <summary>The mean number of people who appear per minute; arrivals are random (a Poisson process).</summary>
    public double ArrivalsPerMinute { get; init; } = 6;

    /// <summary>The most simulated people in the building at once (calling, waiting or riding); arrivals beyond it are skipped.</summary>
    public int MaxActivePeople { get; init; } = 20;

    /// <summary>The most people the generator creates in total, or <see langword="null"/> to keep going until stopped.</summary>
    public int? MaxPeople { get; init; }

    /// <summary>Seed for the random source: the same seed gives the same people at the same times.</summary>
    public int Seed { get; init; } = 2026;

    /// <summary>Throws if any setting is out of range.</summary>
    public void Validate()
    {
        if (!double.IsFinite(ArrivalsPerMinute) || ArrivalsPerMinute <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ArrivalsPerMinute), ArrivalsPerMinute, "The arrival rate must be a positive number.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(MaxActivePeople, 1, nameof(MaxActivePeople));
        if (MaxPeople is { } maxPeople)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxPeople, 0, nameof(MaxPeople));
        }
    }
}
