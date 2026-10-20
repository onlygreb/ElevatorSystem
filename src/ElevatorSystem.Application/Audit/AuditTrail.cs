using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace ElevatorSystem.Application.Audit;

/// <summary>
/// The tamper-evident audit trail (ADR-0014, ADR-0023): appends hash-chained entries, verifies the chain and
/// anonymises a staff member's entries without breaking it. Appends are serialised in this process, and the
/// store refuses a duplicate sequence, so a second writer elsewhere is retried rather than forking the chain.
/// </summary>
public sealed class AuditTrail : IDisposable
{
    public const int MaxActionLength = 64;
    public const int MaxTargetLength = 200;
    public const int MaxOutcomeLength = 32;
    public const int MaxReferenceLength = 64;
    public const int MaxAppendAttempts = 3;
    public const int VerificationBatchSize = 500;

    private readonly IAuditStore _store;
    private readonly ClientPseudonymizer _pseudonymizer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuditTrail> _logger;
    private readonly SemaphoreSlim _appendGate = new(1, 1);

    public AuditTrail(IAuditStore store, ClientPseudonymizer pseudonymizer, TimeProvider timeProvider, ILogger<AuditTrail> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _pseudonymizer = pseudonymizer ?? throw new ArgumentNullException(nameof(pseudonymizer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Appends one entry and returns it. Throws if it cannot be stored, so callers fail closed.</summary>
    public async Task<AuditEntry> RecordAsync(AuditRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        RequireText(record.Action, MaxActionLength, nameof(record.Action));
        RequireText(record.Target, MaxTargetLength, nameof(record.Target), allowEmpty: true);
        RequireText(record.Outcome, MaxOutcomeLength, nameof(record.Outcome));

        var pseudonym = record.ClientAddress is { } address ? _pseudonymizer.Pseudonymise(address) : null;

        await _appendGate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var entry = Link(await _store.GetLastAsync(cancellationToken), record, pseudonym);
                try
                {
                    await _store.AppendAsync(entry, cancellationToken);
                    AuditLog.Recorded(_logger, entry.Sequence, entry.Action, entry.Outcome);
                    return entry;
                }
                catch (AuditSequenceConflictException) when (attempt < MaxAppendAttempts)
                {
                    AuditLog.AppendConflict(_logger, entry.Sequence, attempt);
                }
            }
        }
        finally
        {
            _appendGate.Release();
        }
    }

    /// <summary>Reads the whole chain, oldest first, and checks every link.</summary>
    public async Task<AuditVerification> VerifyAsync(CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> previousHash = AuditChain.Genesis;
        long lastSequence = 0;
        long checkedEntries = 0;

        while (true)
        {
            var batch = await _store.ReadForwardAsync(lastSequence, VerificationBatchSize, cancellationToken);
            foreach (var entry in batch)
            {
                checkedEntries++;
                if (AuditChain.Check(entry, previousHash.Span, lastSequence + 1) is { } fault)
                {
                    // For a gap, the first position that does not verify is the missing one.
                    var broken = fault == AuditChainFault.SequenceGap ? lastSequence + 1 : entry.Sequence;
                    AuditLog.VerificationFailed(_logger, broken, fault);
                    return new AuditVerification(false, checkedEntries, null, ReadOnlyMemory<byte>.Empty, broken, fault, _timeProvider.GetUtcNow());
                }

                previousHash = entry.EntryHash;
                lastSequence = entry.Sequence;
            }

            if (batch.Count < VerificationBatchSize)
            {
                return new AuditVerification(
                    true,
                    checkedEntries,
                    checkedEntries == 0 ? null : lastSequence,
                    checkedEntries == 0 ? ReadOnlyMemory<byte>.Empty : previousHash,
                    null,
                    null,
                    _timeProvider.GetUtcNow());
            }
        }
    }

    /// <summary>The newest entries first, before <paramref name="beforeSequence"/> when given.</summary>
    public Task<IReadOnlyList<AuditEntry>> ReadPageAsync(long? beforeSequence, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        return _store.ReadBackwardAsync(beforeSequence, limit, cancellationToken);
    }

    /// <summary>
    /// Erases a staff member's personal fields from every entry (an LGPD deletion request, ADR-0014) and records
    /// that it happened. The chain still verifies afterwards.
    /// </summary>
    /// <returns>How many entries were anonymised.</returns>
    public async Task<int> AnonymiseActorAsync(Guid actorId, string reference, CancellationToken cancellationToken)
    {
        RequireText(reference, MaxReferenceLength, nameof(reference));

        var anonymised = await _store.AnonymiseActorAsync(actorId, _timeProvider.GetUtcNow(), reference, cancellationToken);
        await RecordAsync(
            new AuditRecord(
                AuditActions.EntriesAnonymised,
                string.Create(CultureInfo.InvariantCulture, $"request {reference}: {anonymised} entries"),
                AuditOutcomes.Succeeded),
            cancellationToken);
        return anonymised;
    }

    public void Dispose() => _appendGate.Dispose();

    private AuditEntry Link(AuditEntry? last, AuditRecord record, string? pseudonym)
    {
        var sequence = (last?.Sequence ?? 0) + 1;
        var previousHash = last?.EntryHash ?? AuditChain.Genesis;
        var now = _timeProvider.GetUtcNow();
        var recordedAt = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        var salt = RandomNumberGenerator.GetBytes(AuditChain.SaltSize);
        var digest = AuditChain.ComputePersonalDigest(salt, record.ActorId, pseudonym);

        return new AuditEntry
        {
            Sequence = sequence,
            RecordedAt = recordedAt,
            Action = record.Action,
            Target = record.Target,
            Outcome = record.Outcome,
            ActorId = record.ActorId,
            ClientPseudonym = pseudonym,
            Salt = salt,
            PersonalDigest = digest,
            EntryHash = AuditChain.ComputeEntryHash(
                previousHash.Span, sequence, recordedAt, record.Action, record.Target, record.Outcome, digest),
        };
    }

    private static void RequireText(string value, int maxLength, string name, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if ((!allowEmpty && value.Length == 0) || value.Length > maxLength)
        {
            throw new ArgumentException($"{name} must be {(allowEmpty ? 0 : 1)}-{maxLength} characters.", name);
        }
    }
}
