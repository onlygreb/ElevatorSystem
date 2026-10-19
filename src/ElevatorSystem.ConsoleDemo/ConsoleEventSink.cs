using System.Globalization;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;

namespace ElevatorSystem.ConsoleDemo;

/// <summary>Prints every elevator event and lets the tour wait for the moments it narrates.</summary>
internal sealed class ConsoleEventSink(ConsoleOutput output) : IElevatorEventSink
{
    private static readonly TimeSpan WaitGuard = TimeSpan.FromSeconds(30);
    private readonly Lock _gate = new();
    private readonly List<(Func<ElevatorEvent, bool> Match, TaskCompletionSource Signal)> _waiters = [];
    private readonly HashSet<RequestId> _queuedRequests = [];
    private int _assignments;
    private int _servedOnArrival;
    private TimeSpan _slowestAssignment;
    private volatile bool _quietRequests;

    /// <summary>When set, requests are counted but not printed as they are queued, merged or served on arrival (used for the burst).</summary>
    public bool QuietRequests
    {
        get => _quietRequests;
        set => _quietRequests = value;
    }

    /// <summary>Requests served the moment they arrived because the door was already open at their floor.</summary>
    public int ServedOnArrival
    {
        get
        {
            lock (_gate)
            {
                return _servedOnArrival;
            }
        }
    }

    public TimeSpan SlowestAssignment
    {
        get
        {
            lock (_gate)
            {
                return _slowestAssignment;
            }
        }
    }

    public void Publish(ElevatorEvent elevatorEvent)
    {
        var servedOnArrival = false;
        lock (_gate)
        {
            switch (elevatorEvent)
            {
                case CallQueued queued:
                    _queuedRequests.Add(queued.Request.Id);
                    RecordAssignment(queued.OccurredAt - queued.Request.RequestedAt);
                    break;
                case CallMerged merged:
                    RecordAssignment(merged.OccurredAt - merged.Request.RequestedAt);
                    break;
                case CallServed served when !_queuedRequests.Remove(served.Call.FirstRequestId):
                    // Never queued: the door was already open at that floor, so it was served on arrival.
                    servedOnArrival = true;
                    _servedOnArrival++;
                    RecordAssignment(served.WaitTime);
                    break;
            }
        }

        if (!(_quietRequests && servedOnArrival))
        {
            Print(elevatorEvent);
        }

        var released = new List<TaskCompletionSource>();
        lock (_gate)
        {
            for (var index = _waiters.Count - 1; index >= 0; index--)
            {
                if (_waiters[index].Match(elevatorEvent))
                {
                    released.Add(_waiters[index].Signal);
                    _waiters.RemoveAt(index);
                }
            }
        }

        released.ForEach(signal => signal.TrySetResult());
    }

    /// <summary>Completes on the next event matching <paramref name="match"/>; register it before causing the event.</summary>
    public Task WaitFor(Func<ElevatorEvent, bool> match, CancellationToken cancellationToken)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _waiters.Add((match, signal));
        }

        return Guarded(signal.Task, cancellationToken);
    }

    /// <summary>Resets the assignment statistics and returns a task that completes after <paramref name="count"/> more assignments.</summary>
    public Task ExpectAssignments(int count, CancellationToken cancellationToken)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            var target = _assignments + count;
            _slowestAssignment = TimeSpan.Zero;
            _servedOnArrival = 0;
            _waiters.Add((_ => _assignments >= target, signal));
        }

        return Guarded(signal.Task, cancellationToken);
    }

    /// <summary>Fails with a clear message instead of hanging if an expected moment never comes.</summary>
    private static Task Guarded(Task task, CancellationToken cancellationToken) =>
        task.WaitAsync(WaitGuard, TimeProvider.System, cancellationToken);

    private void RecordAssignment(TimeSpan latency)
    {
        _slowestAssignment = latency > _slowestAssignment ? latency : _slowestAssignment;
        _assignments++;
    }

    private void Print(ElevatorEvent elevatorEvent)
    {
        switch (elevatorEvent)
        {
            case CallQueued queued when !_quietRequests:
                output.Event("queued", ConsoleColor.Cyan, $"{queued.Request.Call} (position {queued.Position})");
                break;
            case CallMerged merged when !_quietRequests:
                output.Event("merged", ConsoleColor.DarkCyan, $"{merged.Request.Call} (already pending at position {merged.Position})");
                break;
            case RequestRejected rejected:
                output.Event("refused", ConsoleColor.Red, $"{rejected.Request.Call}: {rejected.Reason}");
                break;
            case ElevatorMoved moved:
                output.Event("moving", ConsoleColor.Yellow, $"{(moved.Direction == Direction.Up ? "up" : "down")} {moved.From} -> {moved.Floor}");
                break;
            case DoorsOpened opened:
                output.Event("doors open", ConsoleColor.Green, $"floor {opened.Floor}");
                break;
            case CallServed served:
                output.Event(
                    "served",
                    ConsoleColor.Green,
                    string.Create(CultureInfo.InvariantCulture, $"{served.Call.Call} after {served.WaitTime.TotalSeconds:0.0} s ({served.Call.RequestCount} request(s))"));
                break;
            case DoorsClosed closed:
                output.Event("doors shut", ConsoleColor.DarkGray, $"floor {closed.Floor}");
                break;
            case ElevatorTakenOutOfService outOfService:
                output.Event(
                    "OUT OF SERVICE",
                    ConsoleColor.Red,
                    $"{outOfService.Reason} at floor {outOfService.Floor}; {outOfService.CancelledCalls.Length} pending call(s) cancelled");
                break;
            case ElevatorReturnedToService returned:
                output.Event("in service", ConsoleColor.Green, $"at floor {returned.Floor}");
                break;
        }
    }
}
