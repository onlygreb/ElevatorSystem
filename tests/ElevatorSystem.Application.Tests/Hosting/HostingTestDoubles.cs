using ElevatorSystem.Application.InMemory.Persistence;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using Microsoft.Extensions.Logging;

namespace ElevatorSystem.Application.Tests.Hosting;

/// <summary>Records messages in arrival order and lets a test wait for one, bounded by a real-time guard.</summary>
internal sealed class MessageRecorder<T>
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private readonly Lock _gate = new();
    private readonly List<T> _messages = [];
    private TaskCompletionSource _changed = NewSignal();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _messages.Count;
            }
        }
    }

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

    /// <summary>Waits for the first message at or after <paramref name="fromIndex"/> that matches, and returns it.</summary>
    public async Task<T> WaitForAsync(Func<T, bool> match, int fromIndex = 0, string? description = null)
    {
        using var guard = new CancellationTokenSource(Guard);
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                for (var i = fromIndex; i < _messages.Count; i++)
                {
                    if (match(_messages[i]))
                    {
                        return _messages[i];
                    }
                }

                changed = _changed.Task;
            }

            try
            {
                await changed.WaitAsync(guard.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"Timed out waiting for {description ?? "a matching message"}. Received:{Environment.NewLine}{string.Join(Environment.NewLine, Messages)}");
            }
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>A state store whose saves wait until the test opens the gate.</summary>
internal sealed class GatedStateStore : IElevatorStateStore
{
    private readonly InMemoryElevatorStateStore _inner = new();
    private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _saves;

    public int SaveAttempts => Volatile.Read(ref _saves);

    public void Open() => _open.TrySetResult();

    public async Task SaveAsync(ElevatorSnapshot snapshot, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _saves);
        await _open.Task.WaitAsync(cancellationToken);
        await _inner.SaveAsync(snapshot, cancellationToken);
    }

    public Task<ElevatorSnapshot?> LoadAsync(CancellationToken cancellationToken) => _inner.LoadAsync(cancellationToken);
}

/// <summary>A state store that fails while <see cref="IsDown"/> is set, as an unreachable database would.</summary>
internal sealed class FlakyStateStore : IElevatorStateStore
{
    private readonly InMemoryElevatorStateStore _inner = new();
    private int _failedSaves;

    public bool IsDown { get; set; }

    public int FailedSaves => Volatile.Read(ref _failedSaves);

    public Task SaveAsync(ElevatorSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (IsDown)
        {
            Interlocked.Increment(ref _failedSaves);
            return Task.FromException(new InvalidOperationException("Database unreachable"));
        }

        return _inner.SaveAsync(snapshot, cancellationToken);
    }

    public Task<ElevatorSnapshot?> LoadAsync(CancellationToken cancellationToken) =>
        IsDown ? Task.FromException<ElevatorSnapshot?>(new InvalidOperationException("Database unreachable")) : _inner.LoadAsync(cancellationToken);
}

/// <summary>A deduplication store that is always unreachable.</summary>
internal sealed class UnreachableProcessedCommandStore : IProcessedCommandStore
{
    public Task<bool> TryMarkProcessedAsync(Guid commandId, CancellationToken cancellationToken) =>
        Task.FromException<bool>(new InvalidOperationException("Database unreachable"));
}

/// <summary>An update publisher that refuses everything while <see cref="IsDown"/> is set, as a broker outage would.</summary>
internal sealed class FlakyUpdatePublisher(IElevatorUpdatePublisher inner) : IElevatorUpdatePublisher
{
    private int _refused;

    public bool IsDown { get; set; }

    public int Refused => Volatile.Read(ref _refused);

    public Task PublishAsync(ElevatorUpdateV1 update, CancellationToken cancellationToken) =>
        IsDown ? Refuse() : inner.PublishAsync(update, cancellationToken);

    public Task PublishAsync(OccupancyUpdateV1 update, CancellationToken cancellationToken) =>
        IsDown ? Refuse() : inner.PublishAsync(update, cancellationToken);

    private Task Refuse()
    {
        Interlocked.Increment(ref _refused);
        return Task.FromException(new MessagingUnavailableException("Broker unreachable"));
    }
}

/// <summary>Keeps every log record of every category, to check event IDs and that no message content is logged.</summary>
internal sealed class RecordingLoggerFactory : ILoggerFactory
{
    private readonly Lock _gate = new();
    private readonly List<(string Category, LogLevel Level, EventId EventId, string Message)> _entries = [];

    public IReadOnlyList<(string Category, LogLevel Level, EventId EventId, string Message)> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public bool Contains(int eventId) => Entries.Any(entry => entry.EventId.Id == eventId);

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private void Add(string category, LogLevel level, EventId eventId, string message)
    {
        lock (_gate)
        {
            _entries.Add((category, level, eventId, message));
        }
    }

    private sealed class Logger(RecordingLoggerFactory owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Add(category, logLevel, eventId, formatter(state, exception));
    }
}
