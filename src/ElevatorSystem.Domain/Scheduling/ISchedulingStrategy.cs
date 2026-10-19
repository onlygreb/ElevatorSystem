namespace ElevatorSystem.Domain.Scheduling;

/// <summary>Decides which floor the car heads for next (ADR-0005).</summary>
public interface ISchedulingStrategy
{
    /// <summary>A short name for logs and the operations panel.</summary>
    string Name { get; }

    /// <summary>The floor the car should head for next, or <see langword="null"/> when nothing is pending.</summary>
    Floor? SelectTarget(ElevatorSnapshot snapshot);
}
