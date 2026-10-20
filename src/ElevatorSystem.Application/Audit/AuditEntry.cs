namespace ElevatorSystem.Application.Audit;

/// <summary>
/// One link of the audit chain (ADR-0014). The non-personal fields and <see cref="PersonalDigest"/> are
/// covered by <see cref="EntryHash"/>; the personal fields (<see cref="ActorId"/>, <see cref="ClientPseudonym"/>)
/// are covered only through the salted digest, so they can be erased without breaking the chain.
/// </summary>
public sealed class AuditEntry
{
    /// <summary>Position in the chain, starting at 1 and without gaps.</summary>
    public required long Sequence { get; init; }

    /// <summary>When the entry was written, in UTC, to the millisecond (what every store can keep exactly).</summary>
    public required DateTimeOffset RecordedAt { get; init; }

    /// <summary>What happened, one of <see cref="AuditActions"/>.</summary>
    public required string Action { get; init; }

    /// <summary>What it happened to, for example the service action and its command ID. Never personal data.</summary>
    public required string Target { get; init; }

    /// <summary>How it ended, one of <see cref="AuditOutcomes"/>.</summary>
    public required string Outcome { get; init; }

    /// <summary>The staff member who acted, when known. Personal data; erased by anonymisation.</summary>
    public Guid? ActorId { get; init; }

    /// <summary>The client address as a keyed pseudonym (<see cref="ClientPseudonymizer"/>). Personal data; erased by anonymisation.</summary>
    public string? ClientPseudonym { get; init; }

    /// <summary>The per-entry random salt of <see cref="PersonalDigest"/>; empty once the entry is anonymised.</summary>
    public required ReadOnlyMemory<byte> Salt { get; init; }

    /// <summary><c>SHA-256(salt ‖ personal fields)</c>; kept when the entry is anonymised.</summary>
    public required ReadOnlyMemory<byte> PersonalDigest { get; init; }

    /// <summary><c>SHA-256(previous hash ‖ non-personal fields ‖ personal digest)</c>.</summary>
    public required ReadOnlyMemory<byte> EntryHash { get; init; }

    /// <summary>When the personal fields were erased, or <see langword="null"/> while they are present.</summary>
    public DateTimeOffset? AnonymisedAt { get; init; }

    /// <summary>The reference of the request that erased them, for example a data subject request ID.</summary>
    public string? AnonymisationReference { get; init; }

    public bool IsAnonymised => AnonymisedAt is not null;
}

/// <summary>What a caller asks <see cref="AuditTrail"/> to record; the trail adds the sequence, time, salt and hashes.</summary>
/// <param name="Action">One of <see cref="AuditActions"/>.</param>
/// <param name="Target">What the action applied to; at most <see cref="AuditTrail.MaxTargetLength"/> characters and never personal data.</param>
/// <param name="Outcome">One of <see cref="AuditOutcomes"/>.</param>
/// <param name="ActorId">The staff member, when known.</param>
/// <param name="ClientAddress">The caller's address; only its pseudonym is stored.</param>
public sealed record AuditRecord(
    string Action,
    string Target,
    string Outcome,
    Guid? ActorId = null,
    System.Net.IPAddress? ClientAddress = null);

/// <summary>The actions the audit trail records. Stored as text, so existing values never change.</summary>
public static class AuditActions
{
    public const string SignIn = "staff.sign-in";
    public const string SignOut = "staff.sign-out";
    public const string TwoFactorEnabled = "staff.two-factor-enabled";
    public const string ServiceAction = "elevator.service-action";
    public const string DemoScenario = "elevator.demo-scenario";

    /// <summary>An admin switched the scheduling strategy (ADR-0033).</summary>
    public const string Scheduling = "elevator.scheduling";
    public const string EntriesAnonymised = "audit.entries-anonymised";
}

/// <summary>How an audited action ended. Stored as text, so existing values never change.</summary>
public static class AuditOutcomes
{
    public const string Succeeded = "succeeded";

    /// <summary>Wrong credentials or code.</summary>
    public const string Failed = "failed";

    /// <summary>The account is locked out after too many failures.</summary>
    public const string LockedOut = "locked-out";

    /// <summary>A service action passed the checks and is about to be sent to the engine.</summary>
    public const string Requested = "requested";

    /// <summary>A requested service action could not be sent to the engine.</summary>
    public const string Refused = "refused";
}
