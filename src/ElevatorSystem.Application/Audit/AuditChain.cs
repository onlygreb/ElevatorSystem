using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ElevatorSystem.Application.Audit;

/// <summary>
/// The hash chain of ADR-0014: canonical encoding, digests and the check of one link. The encoding is
/// fixed here and pinned by known-vector tests, so an entry hashed today still verifies after any refactor.
/// </summary>
/// <remarks>
/// Canonical encoding, version <see cref="FormatVersion"/>: integers are big-endian; a text field is its
/// UTF-8 length as a 32-bit integer followed by the bytes, and a missing field is the length <c>-1</c>.
/// <list type="bullet">
///   <item>Personal fields: actor ID (as text, format <c>D</c>), client pseudonym.</item>
///   <item>Personal digest: <c>SHA-256(salt ‖ personal fields)</c>.</item>
///   <item>Non-personal fields: version byte, sequence (64-bit), time in Unix milliseconds (64-bit), action, target, outcome.</item>
///   <item>Entry hash: <c>SHA-256(previous hash ‖ non-personal fields ‖ personal digest)</c>.</item>
/// </list>
/// The first entry's previous hash is <see cref="Genesis"/>, 32 zero bytes.
/// </remarks>
public static class AuditChain
{
    public const byte FormatVersion = 1;
    public const int HashSize = SHA256.HashSizeInBytes;
    public const int SaltSize = 32;

    /// <summary>The previous hash of the first entry.</summary>
    public static ReadOnlyMemory<byte> Genesis { get; } = new byte[HashSize];

    public static byte[] ComputePersonalDigest(ReadOnlySpan<byte> salt, Guid? actorId, string? clientPseudonym)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        buffer.Write(salt);
        WriteText(buffer, actorId?.ToString("D", CultureInfo.InvariantCulture));
        WriteText(buffer, clientPseudonym);
        return SHA256.HashData(buffer.WrittenSpan);
    }

    public static byte[] ComputeEntryHash(
        ReadOnlySpan<byte> previousHash,
        long sequence,
        DateTimeOffset recordedAt,
        string action,
        string target,
        string outcome,
        ReadOnlySpan<byte> personalDigest)
    {
        if (previousHash.Length != HashSize)
        {
            throw new ArgumentException($"A previous hash is {HashSize} bytes.", nameof(previousHash));
        }

        var buffer = new ArrayBufferWriter<byte>(256);
        buffer.Write(previousHash);
        buffer.Write([FormatVersion]);
        WriteInt64(buffer, sequence);
        WriteInt64(buffer, recordedAt.ToUnixTimeMilliseconds());
        WriteText(buffer, action);
        WriteText(buffer, target);
        WriteText(buffer, outcome);
        buffer.Write(personalDigest);
        return SHA256.HashData(buffer.WrittenSpan);
    }

    /// <summary>
    /// Checks one link: its sequence follows the previous one, its personal digest matches its personal fields
    /// (unless it is anonymised, when the stored digest stands in for them), and its hash matches.
    /// </summary>
    /// <returns>The fault, or <see langword="null"/> when the link is intact.</returns>
    public static AuditChainFault? Check(AuditEntry entry, ReadOnlySpan<byte> previousHash, long expectedSequence)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Sequence != expectedSequence)
        {
            return AuditChainFault.SequenceGap;
        }

        if (!entry.IsAnonymised
            && !ComputePersonalDigest(entry.Salt.Span, entry.ActorId, entry.ClientPseudonym).AsSpan().SequenceEqual(entry.PersonalDigest.Span))
        {
            return AuditChainFault.PersonalDigestMismatch;
        }

        var expectedHash = ComputeEntryHash(
            previousHash, entry.Sequence, entry.RecordedAt, entry.Action, entry.Target, entry.Outcome, entry.PersonalDigest.Span);
        return expectedHash.AsSpan().SequenceEqual(entry.EntryHash.Span) ? null : AuditChainFault.EntryHashMismatch;
    }

    private static void WriteInt64(ArrayBufferWriter<byte> buffer, long value)
    {
        BinaryPrimitives.WriteInt64BigEndian(buffer.GetSpan(sizeof(long)), value);
        buffer.Advance(sizeof(long));
    }

    private static void WriteText(ArrayBufferWriter<byte> buffer, string? text)
    {
        if (text is null)
        {
            BinaryPrimitives.WriteInt32BigEndian(buffer.GetSpan(sizeof(int)), -1);
            buffer.Advance(sizeof(int));
            return;
        }

        var length = Encoding.UTF8.GetByteCount(text);
        BinaryPrimitives.WriteInt32BigEndian(buffer.GetSpan(sizeof(int)), length);
        buffer.Advance(sizeof(int));
        buffer.Advance(Encoding.UTF8.GetBytes(text, buffer.GetSpan(length)));
    }
}

/// <summary>Why a link of the chain does not verify.</summary>
public enum AuditChainFault
{
    /// <summary>An entry is missing, duplicated or out of order.</summary>
    SequenceGap,

    /// <summary>The personal fields were changed (and the entry is not marked as anonymised).</summary>
    PersonalDigestMismatch,

    /// <summary>A non-personal field, the digest or an earlier entry was changed.</summary>
    EntryHashMismatch,
}

/// <summary>The result of verifying the whole chain.</summary>
/// <param name="IsIntact">Every link verified.</param>
/// <param name="EntriesChecked">How many entries were read, including the broken one.</param>
/// <param name="HeadSequence">The last entry's sequence, or <see langword="null"/> for an empty trail.</param>
/// <param name="HeadHash">The last entry's hash; worth noting down outside the system to detect a truncated tail.</param>
/// <param name="FirstBrokenSequence">The first entry that failed, if any.</param>
/// <param name="Fault">Why it failed, if it did.</param>
/// <param name="VerifiedAt">When the verification finished.</param>
public sealed record AuditVerification(
    bool IsIntact,
    long EntriesChecked,
    long? HeadSequence,
    ReadOnlyMemory<byte> HeadHash,
    long? FirstBrokenSequence,
    AuditChainFault? Fault,
    DateTimeOffset VerifiedAt);
