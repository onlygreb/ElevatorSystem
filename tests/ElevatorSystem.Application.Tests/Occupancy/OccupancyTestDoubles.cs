using ElevatorSystem.Application.Occupancy;
using ElevatorSystem.Contracts;
using Microsoft.Extensions.Logging;

namespace ElevatorSystem.Application.Tests.Occupancy;

/// <summary>A logger that keeps every entry so tests can check event IDs, levels and messages.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly Lock _gate = new();
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _entries.Add(new LogEntry(logLevel, eventId.Id, formatter(state, exception), exception));
        }
    }

    public IReadOnlyList<LogEntry> WithId(int eventId) => [.. Entries.Where(entry => entry.EventId == eventId)];
}

internal sealed record LogEntry(LogLevel Level, int EventId, string Message, Exception? Exception);

/// <summary>Keeps every occupancy notification, in order.</summary>
internal sealed class RecordingObserver : IOccupancyObserver
{
    private readonly Lock _gate = new();
    private readonly List<BuildingOccupancyDto> _changes = [];

    public IReadOnlyList<BuildingOccupancyDto> Changes
    {
        get
        {
            lock (_gate)
            {
                return [.. _changes];
            }
        }
    }

    public void OccupancyChanged(BuildingOccupancyDto occupancy)
    {
        lock (_gate)
        {
            _changes.Add(occupancy);
        }
    }
}

/// <summary>An observer that is down.</summary>
internal sealed class ThrowingObserver : IOccupancyObserver
{
    public void OccupancyChanged(BuildingOccupancyDto occupancy) => throw new InvalidOperationException("Hub is down");
}
