namespace ElevatorSystem.Domain.Scheduling;

/// <summary>
/// First in, first out: the oldest pending call is the target, and the car travels to it without
/// stopping at other pending floors on the way.
/// </summary>
public sealed class FifoSchedulingStrategy : ISchedulingStrategy
{
    public string Name => "FIFO";

    public Floor? SelectTarget(ElevatorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return snapshot.PendingCalls.IsEmpty ? null : snapshot.PendingCalls[0].Call.Floor;
    }
}
