using ElevatorSystem.Application.Ports;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;

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

/// <summary>A subscriber that records the event and then throws, as a broken downstream consumer would.</summary>
internal sealed class ThrowingSink(EventProbe probe) : IElevatorEventSink
{
    public void Publish(ElevatorEvent elevatorEvent)
    {
        probe.Publish(elevatorEvent);
        throw new InvalidOperationException("Subscriber is down");
    }
}
