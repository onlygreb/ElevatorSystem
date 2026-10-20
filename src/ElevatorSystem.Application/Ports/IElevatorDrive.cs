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

/// <summary>
/// A drive whose pace can change while it runs: only a simulated one, so a demo can fast-forward the building
/// (ADR-0031). <see cref="ElevatorController.SetSpeed"/> refuses to speed up any other drive.
/// </summary>
public interface IVariableSpeedDrive : IElevatorDrive
{
    /// <summary>Makes every following trip take the normal travel time divided by <paramref name="speed"/>.</summary>
    void SetSpeed(double speed);
}
