using CsCheck;
using ElevatorSystem.Application.Audit;

namespace ElevatorSystem.Application.Tests.Audit;

/// <summary>
/// The canonical encoding and digests of ADR-0014. The known vectors pin the format: if one of them changes,
/// every entry written before the change stops verifying, so the format must get a new version instead.
/// </summary>
public sealed class AuditChainTests
{
    private static readonly byte[] Salt = [.. Enumerable.Range(0, AuditChain.SaltSize).Select(value => (byte)value)];
    private static readonly Guid Actor = Guid.Parse("0192b6a4-3c1e-7d2f-9a40-5e6f7a8b9c0d");
    private static readonly DateTimeOffset At = new(2026, 10, 20, 12, 34, 56, 789, TimeSpan.Zero);

    [Fact]
    public void PersonalDigest_MatchesTheKnownVector()
    {
        var digest = AuditChain.ComputePersonalDigest(Salt, Actor, "k1:00112233445566778899aabbccddeeff");

        Convert.ToHexStringLower(digest).ShouldBe(KnownVectors.PersonalDigest);
    }

    [Fact]
    public void EntryHash_MatchesTheKnownVector()
    {
        var digest = Convert.FromHexString(KnownVectors.PersonalDigest);

        var hash = AuditChain.ComputeEntryHash(AuditChain.Genesis.Span, 1, At, "elevator.service-action", "EmergencyStop command 1", "requested", digest);

        Convert.ToHexStringLower(hash).ShouldBe(KnownVectors.EntryHash);
    }

    [Fact]
    public void Encoding_TellsAMissingFieldFromAnEmptyOne()
    {
        AuditChain.ComputePersonalDigest(Salt, null, null)
            .ShouldNotBe(AuditChain.ComputePersonalDigest(Salt, null, string.Empty));
    }

    [Fact]
    public void Encoding_KeepsFieldBoundaries()
    {
        var digest = new byte[AuditChain.HashSize];

        var first = AuditChain.ComputeEntryHash(AuditChain.Genesis.Span, 1, At, "ab", "c", "d", digest);
        var second = AuditChain.ComputeEntryHash(AuditChain.Genesis.Span, 1, At, "a", "bc", "d", digest);

        first.ShouldNotBe(second, "moving characters between fields must change the hash");
    }

    [Fact]
    public void EntryHash_IgnoresTimeBelowAMillisecond()
    {
        var digest = new byte[AuditChain.HashSize];

        AuditChain.ComputeEntryHash(AuditChain.Genesis.Span, 1, At.AddTicks(1), "a", "b", "c", digest)
            .ShouldBe(AuditChain.ComputeEntryHash(AuditChain.Genesis.Span, 1, At, "a", "b", "c", digest), "stores keep milliseconds");
    }

    [Fact]
    public void Check_AnUntouchedEntry_IsIntact()
    {
        var entry = Entry(sequence: 1);

        AuditChain.Check(entry, AuditChain.Genesis.Span, 1).ShouldBeNull();
    }

    [Fact]
    public void Check_AnyChangeToOneField_IsDetected()
    {
        Gen.Int[0, Mutations.Length - 1].Sample(index =>
        {
            var (mutate, fault) = Mutations[index];
            var tampered = mutate(Entry(sequence: 1));

            AuditChain.Check(tampered, AuditChain.Genesis.Span, 1).ShouldBe(fault);
        });
    }

    [Fact]
    public void Check_AnAnonymisedEntry_VerifiesWithItsStoredDigest()
    {
        var entry = Entry(sequence: 1);
        var anonymised = Copy(entry, actor: null, pseudonym: null, salt: ReadOnlyMemory<byte>.Empty, anonymisedAt: At);

        AuditChain.Check(anonymised, AuditChain.Genesis.Span, 1).ShouldBeNull();
    }

    [Fact]
    public void Check_AnEntryAfterTheWrongPredecessor_IsDetected()
    {
        var entry = Entry(sequence: 1);

        AuditChain.Check(entry, new byte[AuditChain.HashSize].Select(_ => (byte)1).ToArray(), 1).ShouldBe(AuditChainFault.EntryHashMismatch);
    }

    private static readonly (Func<AuditEntry, AuditEntry> Mutate, AuditChainFault Fault)[] Mutations =
    [
        (entry => Copy(entry, sequence: 2), AuditChainFault.SequenceGap),
        (entry => Copy(entry, recordedAt: At.AddMilliseconds(1)), AuditChainFault.EntryHashMismatch),
        (entry => Copy(entry, action: "staff.sign-out"), AuditChainFault.EntryHashMismatch),
        (entry => Copy(entry, target: "ReturnToService command 1"), AuditChainFault.EntryHashMismatch),
        (entry => Copy(entry, outcome: "refused"), AuditChainFault.EntryHashMismatch),
        (entry => Copy(entry, actor: Guid.Empty), AuditChainFault.PersonalDigestMismatch),
        (entry => Copy(entry, pseudonym: "k1:ffffffffffffffffffffffffffffffff"), AuditChainFault.PersonalDigestMismatch),
        (entry => Copy(entry, salt: new byte[AuditChain.SaltSize]), AuditChainFault.PersonalDigestMismatch),
        (entry => Copy(entry, digest: new byte[AuditChain.HashSize]), AuditChainFault.PersonalDigestMismatch),
        // Marking an entry anonymised without erasing the fields still has to match the stored digest.
        (entry => Copy(entry, anonymisedAt: At, digest: new byte[AuditChain.HashSize]), AuditChainFault.EntryHashMismatch),
    ];

    internal static AuditEntry Entry(long sequence, ReadOnlyMemory<byte>? previousHash = null)
    {
        const string Pseudonym = "k1:00112233445566778899aabbccddeeff";
        var digest = AuditChain.ComputePersonalDigest(Salt, Actor, Pseudonym);
        return new AuditEntry
        {
            Sequence = sequence,
            RecordedAt = At,
            Action = "elevator.service-action",
            Target = "EmergencyStop command 1",
            Outcome = "requested",
            ActorId = Actor,
            ClientPseudonym = Pseudonym,
            Salt = Salt,
            PersonalDigest = digest,
            EntryHash = AuditChain.ComputeEntryHash(
                (previousHash ?? AuditChain.Genesis).Span, sequence, At, "elevator.service-action", "EmergencyStop command 1", "requested", digest),
        };
    }

    internal static AuditEntry Copy(
        AuditEntry entry,
        long? sequence = null,
        DateTimeOffset? recordedAt = null,
        string? action = null,
        string? target = null,
        string? outcome = null,
        Optional<Guid?> actor = default,
        Optional<string?> pseudonym = default,
        ReadOnlyMemory<byte>? salt = null,
        ReadOnlyMemory<byte>? digest = null,
        DateTimeOffset? anonymisedAt = null) => new()
        {
            Sequence = sequence ?? entry.Sequence,
            RecordedAt = recordedAt ?? entry.RecordedAt,
            Action = action ?? entry.Action,
            Target = target ?? entry.Target,
            Outcome = outcome ?? entry.Outcome,
            ActorId = actor.HasValue ? actor.Value : entry.ActorId,
            ClientPseudonym = pseudonym.HasValue ? pseudonym.Value : entry.ClientPseudonym,
            Salt = salt ?? entry.Salt,
            PersonalDigest = digest ?? entry.PersonalDigest,
            EntryHash = entry.EntryHash,
            AnonymisedAt = anonymisedAt ?? entry.AnonymisedAt,
            AnonymisationReference = anonymisedAt is null ? entry.AnonymisationReference : "test",
        };

    /// <summary>Lets <see cref="Copy"/> tell "set this to null" from "leave it as it is".</summary>
    internal readonly struct Optional<T>
    {
        private Optional(T value)
        {
            Value = value;
            HasValue = true;
        }

        public T Value { get; }

        public bool HasValue { get; }

        public static implicit operator Optional<T>(T value) => new(value);
    }

    /// <summary>Computed independently of this code, with <c>sha256sum</c> over the bytes laid out by hand.</summary>
    private static class KnownVectors
    {
        public const string PersonalDigest = "99df507453b34c1a09910b4b637faba89fefb8c8ec1f340c1be69f0ec717afca";
        public const string EntryHash = "79d1e6efd30075ca07f3a4c7303cba72b6579df2275c49963bd8080b6c363ace";
    }
}
