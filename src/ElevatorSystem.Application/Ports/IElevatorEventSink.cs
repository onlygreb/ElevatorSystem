using ElevatorSystem.Domain.Events;

namespace ElevatorSystem.Application.Ports;

/// <summary>Receives every elevator event, for example to stream it to browsers or the audit log.</summary>
/// <remarks>
/// The control loop calls <see cref="Publish"/>, so implementations must be thread-safe and return
/// quickly: hand the event to a buffer rather than doing I/O inline. A sink that throws is logged and
/// ignored; it never stops the elevator.
/// </remarks>
public interface IElevatorEventSink
{
    void Publish(ElevatorEvent elevatorEvent);
}
