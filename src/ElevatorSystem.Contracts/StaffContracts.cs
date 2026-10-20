namespace ElevatorSystem.Contracts;

/// <summary>
/// Signs a staff member in. Accounts with two-factor authentication also send <see cref="TwoFactorCode"/> (from
/// their authenticator app) or <see cref="RecoveryCode"/>; without one the answer is <c>401</c> with reason
/// <see cref="SignInFailureReason.TwoFactorRequired"/>.
/// </summary>
public sealed record SignInRequest(string Email, string Password, string? TwoFactorCode = null, string? RecoveryCode = null);

/// <summary>The signed-in staff member, as the UI needs it.</summary>
/// <param name="Email">The account's e-mail, which is also its user name.</param>
/// <param name="Roles">What the account may do.</param>
/// <param name="TwoFactorEnabled">The account has an authenticator app registered.</param>
/// <param name="TwoFactorVerified">This session was signed in with the second factor; every staff capability needs it.</param>
/// <param name="TestAccount">
/// The configured test account, signed in with its password only (ADR-0030): it has every staff capability without a
/// second factor, and the UI says so.
/// </param>
public sealed record StaffSessionDto(string Email, IReadOnlyList<StaffRole> Roles, bool TwoFactorEnabled, bool TwoFactorVerified, bool TestAccount = false);

/// <summary>The antiforgery token to send in the <see cref="HeaderName"/> header of every staff <c>POST</c>.</summary>
public sealed record AntiforgeryTokenDto(string HeaderName, string RequestToken);

/// <summary>A new authenticator key, to scan as <see cref="AuthenticatorUri"/> (a QR code) or type as <see cref="SharedKey"/>.</summary>
public sealed record TwoFactorSetupDto(string SharedKey, string AuthenticatorUri);

/// <summary>A code from the authenticator app, proving the key from <see cref="TwoFactorSetupDto"/> was registered.</summary>
public sealed record EnableTwoFactorRequest(string Code);

/// <summary>One-time recovery codes, shown once: each signs in without the authenticator app.</summary>
public sealed record RecoveryCodesDto(IReadOnlyList<string> RecoveryCodes);

/// <summary>An admin's service action: start maintenance, emergency stop or return to service (ADR-0017).</summary>
public sealed record ServiceActionRequest(ServiceAction Action);

/// <summary>A service action passed to the engine; its effect is reported on the hub as a status change.</summary>
public sealed record ServiceActionAcceptedResponse(Guid CommandId, ServiceAction Action);

/// <summary>One entry of the audit trail.</summary>
/// <param name="Sequence">Position in the hash chain.</param>
/// <param name="RecordedAt">When it was written.</param>
/// <param name="Action">What happened, for example <c>elevator.service-action</c>.</param>
/// <param name="Target">What it happened to.</param>
/// <param name="Outcome">How it ended, for example <c>succeeded</c> or <c>failed</c>.</param>
/// <param name="Actor">The staff member's e-mail, when known and not anonymised.</param>
/// <param name="ClientPseudonym">The keyed pseudonym of the client address, when known and not anonymised.</param>
/// <param name="Anonymised">The personal fields were erased (a deletion request); the entry still verifies.</param>
public sealed record AuditEntryDto(
    long Sequence,
    DateTimeOffset RecordedAt,
    string Action,
    string Target,
    string Outcome,
    string? Actor,
    string? ClientPseudonym,
    bool Anonymised);

/// <summary>A page of the audit trail, newest first; pass <see cref="NextBefore"/> as <c>before</c> for the next page.</summary>
public sealed record AuditPageDto(IReadOnlyList<AuditEntryDto> Entries, long? NextBefore);

/// <summary>The result of checking every link of the audit chain.</summary>
/// <param name="Intact">Every entry verified.</param>
/// <param name="EntriesChecked">How many entries were checked.</param>
/// <param name="HeadSequence">The newest entry, if any.</param>
/// <param name="HeadHash">The newest entry's hash in hex; note it down elsewhere to detect a truncated tail later.</param>
/// <param name="FirstBrokenSequence">The first entry that failed, if any.</param>
/// <param name="Fault">Why it failed: <c>SequenceGap</c>, <c>PersonalDigestMismatch</c> or <c>EntryHashMismatch</c>.</param>
/// <param name="VerifiedAt">When the check finished.</param>
public sealed record AuditVerificationDto(
    bool Intact,
    long EntriesChecked,
    long? HeadSequence,
    string? HeadHash,
    long? FirstBrokenSequence,
    string? Fault,
    DateTimeOffset VerifiedAt);

/// <summary>What a staff account may do (ADR-0022).</summary>
public enum StaffRole
{
    /// <summary>Sees occupancy and the audit trail, and takes the elevator out of service and back.</summary>
    Admin,

    /// <summary>Sees occupancy and the audit trail; changes nothing.</summary>
    Auditor,
}

/// <summary>Why a sign-in was refused; the <c>reason</c> extension of the <c>401</c> problem.</summary>
public enum SignInFailureReason
{
    /// <summary>Unknown e-mail, wrong password or locked account: deliberately not told apart.</summary>
    InvalidCredentials,

    /// <summary>The password was right; send it again with a code from the authenticator app.</summary>
    TwoFactorRequired,

    /// <summary>The password was right but the authenticator or recovery code was not.</summary>
    InvalidTwoFactorCode,
}

/// <summary>Body of <c>POST /api/v1/admin/scheduling</c>: the order the car should serve its calls in (ADR-0033).</summary>
public sealed record SchedulingRequest(Scheduling Scheduling);

/// <summary>The change was sent to the engine; it is reported on the hub as a <c>SchedulingChanged</c> activity.</summary>
public sealed record SchedulingAcceptedResponse(Guid CommandId, Scheduling Scheduling);
