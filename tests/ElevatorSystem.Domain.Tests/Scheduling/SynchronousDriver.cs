using ElevatorSystem.Domain.Events;
using ElevatorSystem.Domain.Scheduling;

namespace ElevatorSystem.Domain.Tests.Scheduling;

/// <summary>
/// Runs an elevator through a fixed workload with no time involved: ask the strategy for a target,
/// move one floor towards it, or open and close the door when there. Used to check what a strategy
/// does over a whole workload, independently of the controller's timing.
/// </summary>
internal static class SynchronousDriver
{
    public static DriveResult Drive(Elevator elevator, ISchedulingStrategy strategy)
    {
        var stops = new List<Floor>();
        var served = 0;
        var reversals = 0;
        Direction? lastDirection = null;

        for (var step = 0; ; step++)
        {
            step.ShouldBeLessThan(1_000, "a fixed workload must finish");
            var snapshot = elevator.Snapshot();
            if (strategy.SelectTarget(snapshot) is not { } target)
            {
                return new DriveResult(stops, served, reversals);
            }

            if (target == snapshot.CurrentFloor)
            {
                var opened = elevator.OpenDoor();
                opened.Succeeded.ShouldBeTrue();
                stops.Add(target);
                served += opened.Events.OfType<CallServed>().Sum(e => e.Call.RequestCount);
                elevator.CloseDoor().Succeeded.ShouldBeTrue();
                continue;
            }

            var direction = target > snapshot.CurrentFloor ? Direction.Up : Direction.Down;
            reversals += lastDirection is not null && lastDirection != direction ? 1 : 0;
            lastDirection = direction;
            var moved = direction == Direction.Up ? elevator.MoveUp() : elevator.MoveDown();
            moved.Succeeded.ShouldBeTrue($"{strategy.Name} asked for an impossible move ({moved.Error})");
        }
    }
}

internal sealed record DriveResult(IReadOnlyList<Floor> Stops, int ServedRequests, int Reversals);
