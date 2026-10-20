using ElevatorSystem.Application.Ports;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;
using ElevatorSystem.Domain.Scheduling;

namespace ElevatorSystem.Application.Tests;

/// <summary>A drive whose motor reports a failure on every trip.</summary>
internal sealed class FailingDrive : IElevatorDrive
{
    public Task MoveOneFloorAsync(Direction direction, CancellationToken cancellationToken) =>
        Task.FromException(new InvalidOperationException("Motor overload"));
}

/// <summary>A drive that throws before even starting the trip.</summary>
internal sealed class ThrowingDrive : IElevatorDrive
{
    public Task MoveOneFloorAsync(Direction direction, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Drive controller unreachable");
}

/// <summary>A scheduler with a bug: fine while nothing is pending, it throws as soon as there is a call to plan.</summary>
internal sealed class BrokenStrategy : ISchedulingStrategy
{
    public string Name => "Broken";

    public Floor? SelectTarget(ElevatorSnapshot snapshot) =>
        snapshot.PendingCalls.IsEmpty ? null : throw new InvalidOperationException("Scheduler bug");
}

/// <summary>A subscriber that records the event and then throws, as a broken downstream consumer would.</summary>
internal sealed class ThrowingSink(EventProbe probe) : IElevatorEventSink
{
    public void Publish(ElevatorEvent elevatorEvent)
    {
        probe.Publish(elevatorEvent);
        throw new InvalidOperationException("Subscriber is down");
    }
}
