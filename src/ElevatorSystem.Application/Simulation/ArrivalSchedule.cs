using ElevatorSystem.Domain;

namespace ElevatorSystem.Application.Simulation;

/// <summary>One simulated person: appears <see cref="Delay"/> after the previous one, on <see cref="Origin"/>, going to <see cref="Destination"/>.</summary>
public sealed record SimulatedArrival(TimeSpan Delay, Floor Origin, Floor Destination)
{
    /// <summary>The hall button this person presses.</summary>
    public Direction Direction => Destination > Origin ? Direction.Up : Direction.Down;
}

/// <summary>
/// The random part of <see cref="TrafficGenerator"/>: who appears, where, going where, and when.
/// Draws come from one seeded <see cref="Random"/> and nothing else, so a seed always gives the same
/// sequence, whatever the elevator does meanwhile.
/// </summary>
public sealed class ArrivalSchedule
{
    private static readonly TimeSpan ShortestGap = TimeSpan.FromMilliseconds(1);
    private readonly Building _building;
    private readonly double _arrivalsPerMinute;
    private readonly Random _random;

    public ArrivalSchedule(Building building, TrafficGeneratorOptions options)
    {
        ArgumentNullException.ThrowIfNull(building);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _building = building;
        _arrivalsPerMinute = options.ArrivalsPerMinute;
        _random = new Random(options.Seed);
    }

    /// <summary>The next person: exponentially distributed gap, uniform origin, uniform destination other than the origin.</summary>
    public SimulatedArrival Next()
    {
        var minutes = -Math.Log(1 - _random.NextDouble()) / _arrivalsPerMinute;
        var gap = TimeSpan.FromMinutes(minutes);
        var lowest = _building.LowestFloor.Number;
        var highest = _building.HighestFloor.Number;
        var origin = _random.Next(lowest, highest + 1);
        var destination = _random.Next(lowest, highest);
        if (destination >= origin)
        {
            destination++;
        }

        return new SimulatedArrival(gap < ShortestGap ? ShortestGap : gap, new Floor(origin), new Floor(destination));
    }
}
