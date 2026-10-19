using ElevatorSystem.Application.Ports;
using ElevatorSystem.Domain;

namespace ElevatorSystem.Application.Simulation;

/// <summary>
/// A drive that takes a fixed time per floor. <see cref="SimulateStuckCar"/> makes the next trip
/// never arrive, so the controller's watchdog can be demonstrated.
/// </summary>
public sealed class SimulatedElevatorDrive : IElevatorDrive
{
    private readonly TimeSpan _travelTimePerFloor;
    private readonly TimeProvider _timeProvider;
    private int _stuckOnNextMove;

    public SimulatedElevatorDrive(TimeSpan travelTimePerFloor, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(travelTimePerFloor, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _travelTimePerFloor = travelTimePerFloor;
        _timeProvider = timeProvider;
    }

    public Task MoveOneFloorAsync(Direction direction, CancellationToken cancellationToken)
    {
        var stuck = Interlocked.Exchange(ref _stuckOnNextMove, 0) == 1;
        return Task.Delay(stuck ? Timeout.InfiniteTimeSpan : _travelTimePerFloor, _timeProvider, cancellationToken);
    }

    /// <summary>Makes the next trip hang until it is cancelled, as if the car were jammed in the shaft.</summary>
    public void SimulateStuckCar() => Interlocked.Exchange(ref _stuckOnNextMove, 1);
}
