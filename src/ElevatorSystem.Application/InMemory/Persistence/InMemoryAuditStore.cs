using ElevatorSystem.Application.Audit;

namespace ElevatorSystem.Application.InMemory.Persistence;

/// <summary>
/// The audit chain in memory, for tests. Thread-safe, and like the database it refuses a second entry with
/// the same sequence.
/// </summary>
public sealed class InMemoryAuditStore : IAuditStore
{
    private readonly Lock _lock = new();
    private readonly SortedList<long, AuditEntry> _entries = [];

    /// <summary>Every entry, oldest first.</summary>
    public IReadOnlyList<AuditEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries.Values];
            }
        }
    }

    public Task<AuditEntry?> GetLastAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            return Task.FromResult(_entries.Count == 0 ? null : _entries.Values[^1]);
        }
    }

    public Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (!_entries.TryAdd(entry.Sequence, entry))
            {
                throw new AuditSequenceConflictException();
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> ReadForwardAsync(long afterSequence, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<AuditEntry>>([.. _entries.Values.Where(entry => entry.Sequence > afterSequence).Take(count)]);
        }
    }

    public Task<IReadOnlyList<AuditEntry>> ReadBackwardAsync(long? beforeSequence, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<AuditEntry>>(
                [.. _entries.Values.Reverse().Where(entry => beforeSequence is null || entry.Sequence < beforeSequence).Take(count)]);
        }
    }

    public Task<int> AnonymiseActorAsync(Guid actorId, DateTimeOffset anonymisedAt, string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var matching = _entries.Values.Where(entry => entry.ActorId == actorId && !entry.IsAnonymised).ToList();
            foreach (var entry in matching)
            {
                _entries[entry.Sequence] = Anonymised(entry, anonymisedAt, reference);
            }

            return Task.FromResult(matching.Count);
        }
    }

    /// <summary>Replaces an entry as it is, bypassing the append-only rule: lets tests tamper with the chain.</summary>
    public void Overwrite(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            _entries[entry.Sequence] = entry;
        }
    }

    /// <summary>Removes an entry, bypassing the append-only rule: lets tests tamper with the chain.</summary>
    public void Remove(long sequence)
    {
        lock (_lock)
        {
            _entries.Remove(sequence);
        }
    }

    private static AuditEntry Anonymised(AuditEntry entry, DateTimeOffset anonymisedAt, string reference) => new()
    {
        Sequence = entry.Sequence,
        RecordedAt = entry.RecordedAt,
        Action = entry.Action,
        Target = entry.Target,
        Outcome = entry.Outcome,
        ActorId = null,
        ClientPseudonym = null,
        Salt = ReadOnlyMemory<byte>.Empty,
        PersonalDigest = entry.PersonalDigest,
        EntryHash = entry.EntryHash,
        AnonymisedAt = anonymisedAt,
        AnonymisationReference = reference,
    };
}
