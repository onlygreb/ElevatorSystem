using Microsoft.Extensions.Logging;

namespace ElevatorSystem.Application.Tests.InMemory.Messaging;

/// <summary>
/// Records what a handler received and lets a test wait for it. Waits are bounded by a real-time guard,
/// so a message that never arrives fails the test with what did arrive.
/// </summary>
internal sealed class MessageInbox<T>
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private readonly Lock _gate = new();
    private readonly List<T> _messages = [];
    private TaskCompletionSource _changed = NewSignal();

    public IReadOnlyList<T> Messages
    {
        get
        {
            lock (_gate)
            {
                return [.. _messages];
            }
        }
    }

    public Task Handle(T message, CancellationToken cancellationToken)
    {
        Add(message);
        return Task.CompletedTask;
    }

    public void Add(T message)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            _messages.Add(message);
            signal = _changed;
            _changed = NewSignal();
        }

        signal.TrySetResult();
    }

    public async Task<IReadOnlyList<T>> WaitForCountAsync(int count)
    {
        using var guard = new CancellationTokenSource(Guard);
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_messages.Count >= count)
                {
                    return [.. _messages];
                }

                changed = _changed.Task;
            }

            try
            {
                await changed.WaitAsync(guard.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Expected {count} message(s) within {Guard}; received {Messages.Count}: {string.Join(", ", Messages)}");
            }
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>A logger that keeps every formatted message, to check what the bus writes to logs.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly Lock _gate = new();
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
