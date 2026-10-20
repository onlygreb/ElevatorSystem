using Microsoft.Extensions.Logging;

namespace ElevatorSystem.Application.Audit;

/// <summary>
/// Source-generated log messages of the audit trail (event IDs 1900-1919). They carry sequences, actions and
/// outcomes, never the actor or the client pseudonym: those live only in the trail (ADR-0009).
/// </summary>
internal static partial class AuditLog
{
    [LoggerMessage(1900, LogLevel.Debug, "Audit entry {Sequence} recorded: {Action} {Outcome}")]
    public static partial void Recorded(ILogger logger, long sequence, string action, string outcome);

    [LoggerMessage(1901, LogLevel.Warning, "Audit entry {Sequence} was taken by another writer (attempt {Attempt}); retrying after the new head")]
    public static partial void AppendConflict(ILogger logger, long sequence, int attempt);

    [LoggerMessage(1902, LogLevel.Critical, "Audit chain verification failed at entry {Sequence}: {Fault}")]
    public static partial void VerificationFailed(ILogger logger, long sequence, AuditChainFault fault);
}
