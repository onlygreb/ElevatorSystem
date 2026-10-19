using System.Collections.Concurrent;
using ElevatorSystem.Application.Ports;

namespace ElevatorSystem.Application.InMemory.Persistence;

/// <summary>
/// Remembers processed command IDs in memory, with the time each was recorded so old entries can
/// be purged. Thread-safe: of any number of concurrent calls with the same ID, exactly one returns
/// <see langword="true"/>.
/// </summary>
public sealed class InMemoryProcessedCommandStore(TimeProvider timeProvider) : IProcessedCommandStore
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _processed = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>How many command IDs are currently remembered.</summary>
    public int Count => _processed.Count;

    public Task<bool> TryMarkProcessedAsync(Guid commandId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_processed.TryAdd(commandId, _timeProvider.GetUtcNow()));
    }

    /// <summary>Forgets every command recorded more than <paramref name="retention"/> ago, and returns how many were removed.</summary>
    public int PurgeExpired(TimeSpan retention)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);

        var cutoff = _timeProvider.GetUtcNow() - retention;
        var removed = 0;
        foreach (var (commandId, processedAt) in _processed)
        {
            if (processedAt < cutoff && _processed.TryRemove(new KeyValuePair<Guid, DateTimeOffset>(commandId, processedAt)))
            {
                removed++;
            }
        }

        return removed;
    }
}
