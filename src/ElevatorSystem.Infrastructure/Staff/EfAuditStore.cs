using ElevatorSystem.Application.Audit;
using Microsoft.EntityFrameworkCore;

namespace ElevatorSystem.Infrastructure.Staff;

/// <summary>
/// The audit chain in <see cref="StaffDbContext"/>. Appends are plain inserts keyed by sequence, so the primary
/// key refuses a fork; the only update is the anonymisation of personal fields (ADR-0014). Works on any EF Core
/// relational provider: PostgreSQL in production, SQLite in single-process mode.
/// </summary>
public sealed class EfAuditStore(IDbContextFactory<StaffDbContext> contextFactory) : IAuditStore
{
    private readonly IDbContextFactory<StaffDbContext> _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    public async Task<AuditEntry?> GetLastAsync(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var record = await context.AuditEntries.AsNoTracking()
            .OrderByDescending(entry => entry.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
        return record is null ? null : ToEntry(record);
    }

    public async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.AuditEntries.Add(ToRecord(entry));
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        // Providers report a key violation differently; whether the sequence now exists is what matters.
        catch (DbUpdateException exception)
        {
            if (await ExistsAsync(entry.Sequence, cancellationToken))
            {
                throw new AuditSequenceConflictException($"Audit entry {entry.Sequence} already exists.", exception);
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<AuditEntry>> ReadForwardAsync(long afterSequence, int count, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var records = await context.AuditEntries.AsNoTracking()
            .Where(entry => entry.Sequence > afterSequence)
            .OrderBy(entry => entry.Sequence)
            .Take(count)
            .ToListAsync(cancellationToken);
        return [.. records.Select(ToEntry)];
    }

    public async Task<IReadOnlyList<AuditEntry>> ReadBackwardAsync(long? beforeSequence, int count, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.AuditEntries.AsNoTracking();
        if (beforeSequence is { } before)
        {
            query = query.Where(entry => entry.Sequence < before);
        }

        var records = await query.OrderByDescending(entry => entry.Sequence).Take(count).ToListAsync(cancellationToken);
        return [.. records.Select(ToEntry)];
    }

    public async Task<int> AnonymiseActorAsync(Guid actorId, DateTimeOffset anonymisedAt, string reference, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.AuditEntries
            .Where(entry => entry.ActorId == actorId && entry.AnonymisedAt == null)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(entry => entry.ActorId, (Guid?)null)
                    .SetProperty(entry => entry.ClientPseudonym, (string?)null)
                    .SetProperty(entry => entry.Salt, (byte[]?)null)
                    .SetProperty(entry => entry.AnonymisedAt, anonymisedAt)
                    .SetProperty(entry => entry.AnonymisationReference, reference),
                cancellationToken);
    }

    private async Task<bool> ExistsAsync(long sequence, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.AuditEntries.AnyAsync(entry => entry.Sequence == sequence, cancellationToken);
    }

    internal static AuditEntryRecord ToRecord(AuditEntry entry) => new()
    {
        Sequence = entry.Sequence,
        RecordedAt = entry.RecordedAt,
        Action = entry.Action,
        Target = entry.Target,
        Outcome = entry.Outcome,
        ActorId = entry.ActorId,
        ClientPseudonym = entry.ClientPseudonym,
        Salt = entry.IsAnonymised ? null : entry.Salt.ToArray(),
        PersonalDigest = entry.PersonalDigest.ToArray(),
        EntryHash = entry.EntryHash.ToArray(),
        AnonymisedAt = entry.AnonymisedAt,
        AnonymisationReference = entry.AnonymisationReference,
    };

    internal static AuditEntry ToEntry(AuditEntryRecord record) => new()
    {
        Sequence = record.Sequence,
        RecordedAt = record.RecordedAt,
        Action = record.Action,
        Target = record.Target,
        Outcome = record.Outcome,
        ActorId = record.ActorId,
        ClientPseudonym = record.ClientPseudonym,
        Salt = record.Salt ?? ReadOnlyMemory<byte>.Empty,
        PersonalDigest = record.PersonalDigest,
        EntryHash = record.EntryHash,
        AnonymisedAt = record.AnonymisedAt,
        AnonymisationReference = record.AnonymisationReference,
    };
}
