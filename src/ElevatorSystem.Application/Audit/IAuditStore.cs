namespace ElevatorSystem.Application.Audit;

/// <summary>
/// Append-only storage of the audit chain. Implementations never update or delete an entry, except to erase
/// its personal fields in <see cref="AnonymiseActorAsync"/>, and they refuse a second entry with the same
/// sequence, so two writers can never fork the chain (ADR-0023).
/// </summary>
public interface IAuditStore
{
    /// <summary>The entry with the highest sequence, or <see langword="null"/> when the trail is empty.</summary>
    Task<AuditEntry?> GetLastAsync(CancellationToken cancellationToken);

    /// <summary>Stores a new entry.</summary>
    /// <exception cref="AuditSequenceConflictException">An entry with the same sequence already exists.</exception>
    Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken);

    /// <summary>Up to <paramref name="count"/> entries after <paramref name="afterSequence"/>, oldest first.</summary>
    Task<IReadOnlyList<AuditEntry>> ReadForwardAsync(long afterSequence, int count, CancellationToken cancellationToken);

    /// <summary>
    /// Up to <paramref name="count"/> entries before <paramref name="beforeSequence"/> (or from the newest, when
    /// <see langword="null"/>), newest first.
    /// </summary>
    Task<IReadOnlyList<AuditEntry>> ReadBackwardAsync(long? beforeSequence, int count, CancellationToken cancellationToken);

    /// <summary>
    /// Erases the actor, client pseudonym and salt of every entry by <paramref name="actorId"/> that still has them,
    /// marking each as anonymised at <paramref name="anonymisedAt"/> for <paramref name="reference"/>. Hashes and
    /// digests are left as they are.
    /// </summary>
    /// <returns>How many entries were anonymised.</returns>
    Task<int> AnonymiseActorAsync(Guid actorId, DateTimeOffset anonymisedAt, string reference, CancellationToken cancellationToken);
}

/// <summary>Another writer appended an entry with the same sequence first.</summary>
public sealed class AuditSequenceConflictException : Exception
{
    public AuditSequenceConflictException()
        : this("Another entry with the same sequence was appended first.")
    {
    }

    public AuditSequenceConflictException(string message)
        : base(message)
    {
    }

    public AuditSequenceConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
