namespace ElevatorSystem.Domain;

/// <summary>The range of floors an elevator serves.</summary>
public sealed record Building
{
    public Building(int lowestFloor, int highestFloor)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(lowestFloor, highestFloor);

        LowestFloor = new Floor(lowestFloor);
        HighestFloor = new Floor(highestFloor);
    }

    /// <summary>Floors 1 to 10.</summary>
    public static Building Default { get; } = new(1, 10);

    public Floor LowestFloor { get; }

    public Floor HighestFloor { get; }

    public int FloorCount => HighestFloor.Number - LowestFloor.Number + 1;

    /// <summary>
    /// The most calls that can be pending at once: a hall call in each possible direction on every
    /// floor (none going up from the top, none going down from the bottom) plus a car call to every floor.
    /// </summary>
    public int MaxPendingCalls => (2 * FloorCount) - 2 + FloorCount;

    public bool Contains(Floor floor) => floor >= LowestFloor && floor <= HighestFloor;

    /// <summary>Whether a car at <paramref name="floor"/> can travel in <paramref name="direction"/>.</summary>
    public bool CanTravel(Floor floor, Direction direction) => direction switch
    {
        Direction.Up => floor < HighestFloor,
        Direction.Down => floor > LowestFloor,
        _ => false,
    };

    /// <summary>Checks that a call can be served in this building.</summary>
    /// <returns>Why the call is invalid, or <see langword="null"/> when it is valid.</returns>
    public RequestRejectionReason? Validate(ElevatorCall call)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (!Contains(call.Floor))
        {
            return RequestRejectionReason.InvalidFloor;
        }

        return call is HallCall hall && !CanTravel(hall.Floor, hall.Direction)
            ? RequestRejectionReason.InvalidDirection
            : null;
    }
}
