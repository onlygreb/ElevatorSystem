using ElevatorSystem.Application.Ports;
using ElevatorSystem.Domain.Events;

namespace ElevatorSystem.Application.Tests;

/// <summary>
/// An event sink that records every event and lets a test wait for the ones it expects.
/// Waits are bounded by a real-time guard so a broken controller fails fast with the events seen.
/// </summary>
internal sealed class EventProbe : IElevatorEventSink
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private readonly Lock _gate = new();
    private readonly List<ElevatorEvent> _events = [];
    private TaskCompletionSource _changed = NewSignal();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _events.Count;
            }
        }
    }

    public IReadOnlyList<ElevatorEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    public void Publish(ElevatorEvent elevatorEvent)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            _events.Add(elevatorEvent);
            signal = _changed;
            _changed = NewSignal();
        }

        signal.TrySetResult();
    }

    public bool TryFind<T>(Func<T, bool>? match, int fromIndex, out T found, out int index)
        where T : ElevatorEvent
    {
        lock (_gate)
        {
            for (var i = fromIndex; i < _events.Count; i++)
            {
                if (_events[i] is T candidate && (match?.Invoke(candidate) ?? true))
                {
                    found = candidate;
                    index = i;
                    return true;
                }
            }
        }

        found = null!;
        index = -1;
        return false;
    }

    /// <summary>Waits for the first event of type <typeparamref name="T"/> at or after <paramref name="fromIndex"/> that matches.</summary>
    public async Task<(T Event, int Index)> WaitForAsync<T>(Func<T, bool>? match = null, int fromIndex = 0)
        where T : ElevatorEvent
    {
        (T, int)? result = null;
        await WaitUntilAsync(
            () =>
            {
                if (TryFind(match, fromIndex, out var found, out var index))
                {
                    result = (found, index);
                }

                return result is not null;
            },
            $"a {typeof(T).Name} matching the condition");
        return result!.Value;
    }

    public Task WaitForCountAsync(int count) => WaitUntilAsync(() => Count >= count, $"{count} events");

    /// <summary>Waits until <paramref name="condition"/> holds, re-checking after every published event.</summary>
    public async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        using var guard = new CancellationTokenSource(Guard);
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                changed = _changed.Task;
            }

            if (condition())
            {
                return;
            }

            try
            {
                await changed.WaitAsync(guard.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Timed out waiting for {description}. Events so far:{Environment.NewLine}{Describe()}");
            }
        }
    }

    public string Describe() => string.Join(Environment.NewLine, Events.Select((e, i) => $"  {i}: {e}"));

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
