using System.Collections.Immutable;

namespace ElevatorSystem.Domain.Scheduling;

/// <summary>
/// LOOK: keep going in the current direction while there are pending floors ahead, stopping at each
/// one, then reverse. Before the first trip the car heads towards the oldest call.
/// </summary>
public sealed class LookSchedulingStrategy : ISchedulingStrategy
{
    public string Name => "LOOK";

    public Floor? SelectTarget(ElevatorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var floors = snapshot.TargetFloors;
        if (floors.IsEmpty)
        {
            return null;
        }

        var current = snapshot.CurrentFloor;
        if (floors.Contains(current))
        {
            return current;
        }

        var direction = snapshot.TravelDirection ?? (floors[0] > current ? Direction.Up : Direction.Down);
        return NearestAhead(floors, current, direction) ?? NearestAhead(floors, current, direction.Opposite());
    }

    private static Floor? NearestAhead(ImmutableArray<Floor> floors, Floor current, Direction direction)
    {
        Floor? nearest = null;
        foreach (var floor in floors)
        {
            var isAhead = direction == Direction.Up ? floor > current : floor < current;
            if (isAhead && (nearest is null || Distance(floor, current) < Distance(nearest.Value, current)))
            {
                nearest = floor;
            }
        }

        return nearest;
    }

    private static int Distance(Floor from, Floor to) => Math.Abs(from.Number - to.Number);
}
