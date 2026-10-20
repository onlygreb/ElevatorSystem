using System.Collections.Concurrent;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;

namespace ElevatorSystem.Application.Hosting;

/// <summary>
/// The controller's sink while this engine leads: every event asks for a snapshot save and becomes an
/// <see cref="ElevatorUpdateV1"/> with the status right after it. Both are hand-offs to background
/// workers, so <see cref="Publish"/> stays fast and never blocks the control loop (ADR-0007).
/// </summary>
/// <remarks>
/// It also tells the command handler when the loop has taken a submitted request in (queued, merged,
/// served or refused), so a graceful stop never leaves an acknowledged command in the controller's intake.
/// </remarks>
internal sealed class EngineEventSink(Elevator elevator, SnapshotPersister persister, ElevatorUpdateForwarder forwarder, TimeProvider timeProvider)
    : IElevatorEventSink
{
    private readonly ConcurrentDictionary<RequestId, TaskCompletionSource> _awaited = new();

    public void Publish(ElevatorEvent elevatorEvent)
    {
        persister.Notify();
        var status = ElevatorContractMapper.ToStatus(elevator.Snapshot());
        forwarder.Enqueue(new ElevatorUpdateV1(Guid.CreateVersion7(timeProvider.GetUtcNow()), ElevatorContractMapper.ToActivity(elevatorEvent), status));

        if (!_awaited.IsEmpty && RequestOf(elevatorEvent) is { } requestId && _awaited.TryRemove(requestId, out var taken))
        {
            taken.TrySetResult();
        }
    }

    /// <summary>
    /// Returns a task that completes when the control loop reports <paramref name="requestId"/>. Call it
    /// before submitting the request, and <see cref="Forget"/> if the submission is refused.
    /// </summary>
    public Task WhenTakenIn(RequestId requestId) =>
        _awaited.GetOrAdd(requestId, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    public void Forget(RequestId requestId) => _awaited.TryRemove(requestId, out _);

    private static RequestId? RequestOf(ElevatorEvent elevatorEvent) => elevatorEvent switch
    {
        CallQueued queued => queued.Request.Id,
        CallMerged merged => merged.Request.Id,
        CallServed served => served.Call.FirstRequestId,
        RequestRejected rejected => rejected.Request.Id,
        _ => null,
    };
}
