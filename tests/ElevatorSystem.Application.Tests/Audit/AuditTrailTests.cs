using System.Net;
using ElevatorSystem.Application.Audit;
using ElevatorSystem.Application.InMemory.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace ElevatorSystem.Application.Tests.Audit;

/// <summary>Appending, verifying and anonymising the audit trail (ADR-0014, ADR-0023), over the in-memory store.</summary>
public sealed class AuditTrailTests : IDisposable
{
    private static readonly Guid Admin = Guid.Parse("0192b6a4-0000-7000-8000-000000000001");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
    private readonly InMemoryAuditStore _store = new();
    private readonly AuditTrail _trail;

    public AuditTrailTests()
    {
        _trail = Trail(_store);
    }

    public void Dispose() => _trail.Dispose();

    [Fact]
    public async Task Record_LinksEachEntryToThePreviousOne()
    {
        var first = await RecordAsync();
        var second = await RecordAsync();

        first.Sequence.ShouldBe(1);
        second.Sequence.ShouldBe(2);
        AuditChain.Check(first, AuditChain.Genesis.Span, 1).ShouldBeNull();
        AuditChain.Check(second, first.EntryHash.Span, 2).ShouldBeNull();
    }

    [Fact]
    public async Task Record_KeepsMillisecondsOnly_SoEveryStoreKeepsTheTimeExactly()
    {
        _time.Advance(TimeSpan.FromTicks(12_345_678));

        var entry = await RecordAsync();

        entry.RecordedAt.Ticks.ShouldBe(entry.RecordedAt.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond);
        entry.RecordedAt.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task Record_StoresAPseudonym_NeverTheAddress()
    {
        var entry = await RecordAsync(address: IPAddress.Parse("203.0.113.9"));

        entry.ClientPseudonym.ShouldNotBeNull().ShouldNotContain("203.0.113.9");
        entry.ClientPseudonym.ShouldStartWith("k1:");
    }

    [Fact]
    public async Task Record_GivesEveryEntryItsOwnSalt()
    {
        var first = await RecordAsync();
        var second = await RecordAsync();

        first.Salt.Length.ShouldBe(AuditChain.SaltSize);
        first.Salt.ToArray().ShouldNotBe(second.Salt.ToArray());
        first.PersonalDigest.ToArray().ShouldNotBe(second.PersonalDigest.ToArray(), "the same actor must not be recognisable across entries");
    }

    [Theory]
    [InlineData("", "target", "succeeded")]
    [InlineData("action", "target", "")]
    [InlineData("an-action-name-that-goes-on-far-longer-than-sixty-four-characters-in-total", "target", "succeeded")]
    public async Task Record_InvalidFields_AreRefused(string action, string target, string outcome)
    {
        await Should.ThrowAsync<ArgumentException>(() => _trail.RecordAsync(new AuditRecord(action, target, outcome), TestContext.Current.CancellationToken));
        _store.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Record_FromManyThreadsAtOnce_FormsOneUnbrokenChain()
    {
        await Parallel.ForAsync(0, 64, TestContext.Current.CancellationToken, async (_, _) => await RecordAsync());

        _store.Entries.Select(entry => entry.Sequence).ShouldBe(Enumerable.Range(1, 64).Select(value => (long)value));
        (await _trail.VerifyAsync(TestContext.Current.CancellationToken)).IsIntact.ShouldBeTrue();
    }

    [Fact]
    public async Task Record_WhenAnotherWriterTookTheSequence_RetriesAfterTheNewHead()
    {
        var store = new RacingStore(_store, racesToLose: 2);
        using var trail = Trail(store);

        var entry = await trail.RecordAsync(new AuditRecord(AuditActions.SignOut, string.Empty, AuditOutcomes.Succeeded), TestContext.Current.CancellationToken);

        entry.Sequence.ShouldBe(3, "two entries were appended by the other writer first");
        (await trail.VerifyAsync(TestContext.Current.CancellationToken)).IsIntact.ShouldBeTrue();
    }

    [Fact]
    public async Task Record_WhenEveryAttemptLosesTheRace_Fails()
    {
        var store = new RacingStore(_store, racesToLose: AuditTrail.MaxAppendAttempts);
        using var trail = Trail(store);

        await Should.ThrowAsync<AuditSequenceConflictException>(
            () => trail.RecordAsync(new AuditRecord(AuditActions.SignOut, string.Empty, AuditOutcomes.Succeeded), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Verify_AnEmptyTrail_IsIntactWithoutAHead()
    {
        var result = await _trail.VerifyAsync(TestContext.Current.CancellationToken);

        result.IsIntact.ShouldBeTrue();
        result.EntriesChecked.ShouldBe(0);
        result.HeadSequence.ShouldBeNull();
        result.HeadHash.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task Verify_ReadsAcrossBatches_AndReportsTheHead()
    {
        AuditEntry last = null!;
        for (var index = 0; index < AuditTrail.VerificationBatchSize + 3; index++)
        {
            last = await RecordAsync();
        }

        var result = await _trail.VerifyAsync(TestContext.Current.CancellationToken);

        result.IsIntact.ShouldBeTrue();
        result.EntriesChecked.ShouldBe(AuditTrail.VerificationBatchSize + 3);
        result.HeadSequence.ShouldBe(last.Sequence);
        result.HeadHash.ToArray().ShouldBe(last.EntryHash.ToArray());
    }

    [Fact]
    public async Task Verify_AChangedEntry_IsReportedWithItsSequence()
    {
        await RecordAsync();
        var second = await RecordAsync();
        await RecordAsync();
        _store.Overwrite(AuditChainTests.Copy(second, outcome: AuditOutcomes.Failed));

        var result = await _trail.VerifyAsync(TestContext.Current.CancellationToken);

        result.IsIntact.ShouldBeFalse();
        result.FirstBrokenSequence.ShouldBe(2);
        result.Fault.ShouldBe(AuditChainFault.EntryHashMismatch);
    }

    [Fact]
    public async Task Verify_ARemovedEntry_IsReportedAsAGapAtItsPosition()
    {
        await RecordAsync();
        await RecordAsync();
        await RecordAsync();
        _store.Remove(2);

        var result = await _trail.VerifyAsync(TestContext.Current.CancellationToken);

        result.FirstBrokenSequence.ShouldBe(2);
        result.Fault.ShouldBe(AuditChainFault.SequenceGap);
    }

    [Fact]
    public async Task AnonymiseActor_ErasesTheirFields_RecordsIt_AndTheChainStillVerifies()
    {
        await RecordAsync(actor: Admin, address: IPAddress.Loopback);
        await RecordAsync(actor: Guid.NewGuid());
        await RecordAsync(actor: Admin);

        var count = await _trail.AnonymiseActorAsync(Admin, "dsr-17", TestContext.Current.CancellationToken);

        count.ShouldBe(2);
        var entries = _store.Entries;
        entries.Where(entry => entry.IsAnonymised).Select(entry => entry.Sequence).ShouldBe([1L, 3L]);
        entries.Where(entry => entry.IsAnonymised).ShouldAllBe(entry => entry.ActorId == null && entry.ClientPseudonym == null && entry.Salt.IsEmpty);
        entries[^1].Action.ShouldBe(AuditActions.EntriesAnonymised);
        entries[^1].Target.ShouldBe("request dsr-17: 2 entries");
        (await _trail.VerifyAsync(TestContext.Current.CancellationToken)).IsIntact.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadPage_ReturnsTheNewestFirst()
    {
        for (var index = 0; index < 5; index++)
        {
            await RecordAsync();
        }

        var page = await _trail.ReadPageAsync(beforeSequence: 4, limit: 2, TestContext.Current.CancellationToken);

        page.Select(entry => entry.Sequence).ShouldBe([3L, 2L]);
    }

    private Task<AuditEntry> RecordAsync(Guid? actor = null, IPAddress? address = null) =>
        _trail.RecordAsync(
            new AuditRecord(AuditActions.ServiceAction, "EmergencyStop command 1", AuditOutcomes.Requested, actor ?? Admin, address),
            TestContext.Current.CancellationToken);

    private AuditTrail Trail(IAuditStore store) => new(
        store,
        new ClientPseudonymizer(Options.Create(new AuditOptions { ClientPseudonymKey = Convert.ToBase64String(new byte[32]) })),
        _time,
        NullLogger<AuditTrail>.Instance);

    /// <summary>Another writer appends just before this one, <c>racesToLose</c> times.</summary>
    private sealed class RacingStore(InMemoryAuditStore inner, int racesToLose) : IAuditStore
    {
        private int _racesLeft = racesToLose;

        public Task<AuditEntry?> GetLastAsync(CancellationToken cancellationToken) => inner.GetLastAsync(cancellationToken);

        public async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
        {
            if (_racesLeft-- > 0)
            {
                var previous = await inner.GetLastAsync(cancellationToken);
                await inner.AppendAsync(AuditChainTests.Entry(entry.Sequence, previous?.EntryHash), cancellationToken);
            }

            await inner.AppendAsync(entry, cancellationToken);
        }

        public Task<IReadOnlyList<AuditEntry>> ReadForwardAsync(long afterSequence, int count, CancellationToken cancellationToken) =>
            inner.ReadForwardAsync(afterSequence, count, cancellationToken);

        public Task<IReadOnlyList<AuditEntry>> ReadBackwardAsync(long? beforeSequence, int count, CancellationToken cancellationToken) =>
            inner.ReadBackwardAsync(beforeSequence, count, cancellationToken);

        public Task<int> AnonymiseActorAsync(Guid actorId, DateTimeOffset anonymisedAt, string reference, CancellationToken cancellationToken) =>
            inner.AnonymiseActorAsync(actorId, anonymisedAt, reference, cancellationToken);
    }
}
