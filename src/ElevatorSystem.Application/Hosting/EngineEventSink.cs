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
/// <para>
/// A press merged into a waiting call changes nothing but its count, so merges are forwarded at most once per
/// <see cref="MergeForwardInterval"/>: a crowd pressing again and again (ADR-0031) must not crowd the car's own steps out
/// of the update buffer. Every merge is still applied, saved and counted; the next update carries the count.
/// </para>
/// <para>
/// It also tells the command handler when the loop has taken a submitted request in (queued, merged,
/// served or refused), so a graceful stop never leaves an acknowledged command in the controller's intake.
/// </para>
/// </remarks>
internal sealed class EngineEventSink(Elevator elevator, SnapshotPersister persister, ElevatorUpdateForwarder forwarder, TimeProvider timeProvider)
    : IElevatorEventSink
{
    public static readonly TimeSpan MergeForwardInterval = TimeSpan.FromMilliseconds(250);

    private readonly ConcurrentDictionary<RequestId, TaskCompletionSource> _awaited = new();
    private DateTimeOffset _lastMergeForwarded = DateTimeOffset.MinValue;

    /// <summary>The controller this sink serves, once created: its door countdown goes into every status.</summary>
    public ElevatorController? Controller { get; set; }

    public void Publish(ElevatorEvent elevatorEvent)
    {
        persister.Notify();
        if (ShouldForward(elevatorEvent))
        {
            var status = CurrentStatus();
            forwarder.Enqueue(new ElevatorUpdateV1(Guid.CreateVersion7(timeProvider.GetUtcNow()), ElevatorContractMapper.ToActivity(elevatorEvent), status));
        }

        if (!_awaited.IsEmpty && RequestOf(elevatorEvent) is { } requestId && _awaited.TryRemove(requestId, out var taken))
        {
            taken.TrySetResult();
        }
    }

    /// <summary>The status now, with the controller's door countdown and speed.</summary>
    public ElevatorStatusDto CurrentStatus() => ElevatorContractMapper.ToStatus(elevator.Snapshot(), Controller?.DoorsCloseAt, Controller?.Speed ?? 1);

    /// <summary>Tells subscribers the car's speed changed (ADR-0031): not an elevator event, so it carries the status as it is.</summary>
    public void PublishSpeedChanged(double speed)
    {
        var status = CurrentStatus();
        var activity = new ElevatorActivityDto(timeProvider.GetUtcNow(), ElevatorActivityKind.SpeedChanged, status.CurrentFloor, Speed: speed);
        forwarder.Enqueue(new ElevatorUpdateV1(Guid.CreateVersion7(timeProvider.GetUtcNow()), activity, status));
    }

    /// <summary>
    /// Returns a task that completes when the control loop reports <paramref name="requestId"/>. Call it
    /// before submitting the request, and <see cref="Forget"/> if the submission is refused.
    /// </summary>
    public Task WhenTakenIn(RequestId requestId) =>
        _awaited.GetOrAdd(requestId, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    public void Forget(RequestId requestId) => _awaited.TryRemove(requestId, out _);

    // Merges come only from the control loop, so the time of the last forwarded one needs no lock.
    private bool ShouldForward(ElevatorEvent elevatorEvent)
    {
        if (elevatorEvent is not CallMerged)
        {
            return true;
        }

        var now = timeProvider.GetUtcNow();
        if (now - _lastMergeForwarded < MergeForwardInterval)
        {
            return false;
        }

        _lastMergeForwarded = now;
        return true;
    }

    private static RequestId? RequestOf(ElevatorEvent elevatorEvent) => elevatorEvent switch
    {
        CallQueued queued => queued.Request.Id,
        CallMerged merged => merged.Request.Id,
        CallServed served => served.Call.FirstRequestId,
        RequestRejected rejected => rejected.Request.Id,
        _ => null,
    };
}
