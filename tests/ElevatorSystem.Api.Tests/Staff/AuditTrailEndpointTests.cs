using System.Net;
using System.Net.Http.Json;
using ElevatorSystem.Application.Audit;
using ElevatorSystem.Contracts;
using ElevatorSystem.Infrastructure.Staff;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ElevatorSystem.Api.Tests.Staff;

/// <summary>
/// The audit trail through the API, over the real staff database (SQLite in single-process mode): paging,
/// verification, and detection of rows changed or removed behind the application's back (ADR-0014, ADR-0023).
/// </summary>
public sealed class AuditTrailEndpointTests
{
    /// <summary>Every entry, newest first.</summary>
    internal static async Task<IReadOnlyList<AuditEntryDto>> EntriesAsync(StaffClient client)
    {
        using var response = await client.GetAsync(ApiRoutes.AuditEntries + "?limit=100");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.AuditPageDto, Http.Cancellation))!.Entries;
    }

    [Fact]
    public async Task Verification_OfAnUntouchedTrail_IsIntactUpToTheNewestEntry()
    {
        await using var factory = new ApiFactory();
        using var auditor = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Auditor);
        var newest = (await EntriesAsync(auditor))[0];

        var verification = await VerifyAsync(auditor);

        verification.Intact.ShouldBeTrue();
        verification.EntriesChecked.ShouldBe(newest.Sequence);
        verification.HeadSequence.ShouldBe(newest.Sequence);
        verification.HeadHash.ShouldNotBeNull().Length.ShouldBe(64);
        verification.FirstBrokenSequence.ShouldBeNull();
    }

    [Theory]
    [InlineData("UPDATE audit_entry SET outcome = 'failed' WHERE sequence = 1", nameof(AuditChainFault.EntryHashMismatch))]
    [InlineData("UPDATE audit_entry SET recorded_at = '2020-01-01 00:00:00+00:00' WHERE sequence = 1", nameof(AuditChainFault.EntryHashMismatch))]
    [InlineData("UPDATE audit_entry SET client_pseudonym = 'k1:00000000000000000000000000000000' WHERE sequence = 1", nameof(AuditChainFault.PersonalDigestMismatch))]
    [InlineData("DELETE FROM audit_entry WHERE sequence = 1", nameof(AuditChainFault.SequenceGap))]
    public async Task Verification_FindsTheFirstEntryChangedInTheDatabase(string tampering, string fault)
    {
        await using var factory = new ApiFactory();
        using var auditor = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Auditor);
        await ExecuteAsync(factory, tampering);

        var verification = await VerifyAsync(auditor);

        verification.Intact.ShouldBeFalse();
        verification.FirstBrokenSequence.ShouldBe(1);
        verification.Fault.ShouldBe(fault);
    }

    [Fact]
    public async Task Verification_FindsAnEntryMovedToAnotherPosition()
    {
        await using var factory = new ApiFactory();
        using var auditor = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Auditor);
        await ExecuteAsync(factory, "UPDATE audit_entry SET sequence = 100 WHERE sequence = 2");
        await ExecuteAsync(factory, "UPDATE audit_entry SET sequence = 2 WHERE sequence = 1");
        await ExecuteAsync(factory, "UPDATE audit_entry SET sequence = 1 WHERE sequence = 100");

        var verification = await VerifyAsync(auditor);

        verification.Intact.ShouldBeFalse();
        verification.FirstBrokenSequence.ShouldBe(1);
    }

    [Fact]
    public async Task Anonymisation_ErasesTheActor_AndTheChainStillVerifies()
    {
        await using var factory = new ApiFactory();
        using (await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Admin))
        {
        }

        using var auditor = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Auditor);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var admin = await scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>().FindByEmailAsync(StaffAccounts.Admin.Email);
            var anonymised = await factory.Services.GetRequiredService<AuditTrail>().AnonymiseActorAsync(admin!.Id, "dsr-2026-001", Http.Cancellation);
            anonymised.ShouldBe(2, "the admin's sign-in and two-factor registration");
        }

        var entries = await EntriesAsync(auditor);
        var verification = await VerifyAsync(auditor);

        entries.ShouldNotContain(entry => entry.Actor == StaffAccounts.Admin.Email);
        entries.Count(entry => entry.Anonymised).ShouldBe(2);
        entries.Where(entry => entry.Anonymised).ShouldAllBe(entry => entry.Actor == null && entry.ClientPseudonym == null);
        entries[0].Action.ShouldBe(AuditActions.EntriesAnonymised);
        entries[0].Target.ShouldBe("request dsr-2026-001: 2 entries");
        verification.Intact.ShouldBeTrue("anonymisation keeps the salted digest the chain covers (ADR-0014)");
    }

    [Fact]
    public async Task Entries_ArePagedNewestFirst()
    {
        await using var factory = new ApiFactory();
        using var auditor = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Auditor);
        using (await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Admin))
        {
        }

        var first = await PageAsync(auditor, "?limit=2");
        var second = await PageAsync(auditor, $"?limit=2&before={first.NextBefore}");

        first.Entries.Select(entry => entry.Sequence).ShouldBe([4, 3]);
        first.NextBefore.ShouldBe(3);
        second.Entries.Select(entry => entry.Sequence).ShouldBe([2, 1]);
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=101")]
    [InlineData("?before=0")]
    [InlineData("?limit=ten")]
    public async Task Entries_WithAnInvalidPage_AreRefused(string query)
    {
        await using var factory = new ApiFactory();
        using var auditor = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Auditor);

        using var response = await auditor.GetAsync(ApiRoutes.AuditEntries + query);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static async Task<AuditPageDto> PageAsync(StaffClient client, string query)
    {
        using var response = await client.GetAsync(ApiRoutes.AuditEntries + query);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.AuditPageDto, Http.Cancellation))!;
    }

    private static async Task<AuditVerificationDto> VerifyAsync(StaffClient client)
    {
        using var response = await client.GetAsync(ApiRoutes.AuditVerification);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.AuditVerificationDto, Http.Cancellation))!;
    }

    /// <summary>Changes the table directly, as someone with database access could.</summary>
    private static async Task ExecuteAsync(ApiFactory factory, string sql)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<StaffDbContext>();
#pragma warning disable EF1002 // Test-only tampering with fixed statements, no input involved.
        (await database.Database.ExecuteSqlRawAsync(sql, Http.Cancellation)).ShouldBe(1);
#pragma warning restore EF1002
    }
}
