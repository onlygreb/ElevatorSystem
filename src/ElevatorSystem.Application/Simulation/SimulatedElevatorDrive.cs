using ElevatorSystem.Application.Ports;
using ElevatorSystem.Domain;

namespace ElevatorSystem.Application.Simulation;

/// <summary>
/// A drive that takes a fixed time per floor, divided by the current speed (ADR-0031). <see cref="SimulateStuckCar"/>
/// makes the next trip never arrive, so the controller's watchdog can be demonstrated.
/// </summary>
public sealed class SimulatedElevatorDrive : IVariableSpeedDrive
{
    private readonly TimeSpan _travelTimePerFloor;
    private readonly TimeProvider _timeProvider;
    private double _speed = 1;
    private int _stuckOnNextMove;

    public SimulatedElevatorDrive(TimeSpan travelTimePerFloor, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(travelTimePerFloor, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _travelTimePerFloor = travelTimePerFloor;
        _timeProvider = timeProvider;
    }

    /// <summary>How many times faster than normal the car travels.</summary>
    public double Speed => Volatile.Read(ref _speed);

    public Task MoveOneFloorAsync(Direction direction, CancellationToken cancellationToken)
    {
        var stuck = Interlocked.Exchange(ref _stuckOnNextMove, 0) == 1;
        return Task.Delay(stuck ? Timeout.InfiniteTimeSpan : _travelTimePerFloor / Speed, _timeProvider, cancellationToken);
    }

    public void SetSpeed(double speed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(speed, 1.0);
        Volatile.Write(ref _speed, speed);
    }

    /// <summary>Makes the next trip hang until it is cancelled, as if the car were jammed in the shaft.</summary>
    public void SimulateStuckCar() => Interlocked.Exchange(ref _stuckOnNextMove, 1);
}
