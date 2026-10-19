using ElevatorSystem.Domain;

namespace ElevatorSystem.Application.Ports;

/// <summary>
/// The machinery that moves the car. A real installation would talk to the motor controller;
/// <see cref="Simulation.SimulatedElevatorDrive"/> waits for the travel time instead.
/// </summary>
public interface IElevatorDrive
{
    /// <summary>Moves the car to the adjacent floor in <paramref name="direction"/> and completes on arrival.</summary>
    Task MoveOneFloorAsync(Direction direction, CancellationToken cancellationToken);
}
