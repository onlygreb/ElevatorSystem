using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Features.Admin;
using ElevatorSystem.Web.Services;

namespace ElevatorSystem.Web.Tests.Features;

/// <summary>The audit trail view: entries newest first, paging, and the result of a chain verification.</summary>
public sealed class AuditTrailPanelTests : WebTestContext
{
    [Fact]
    public void ShowsTheEntries_AndAnonymisedOnesWithoutAnActor()
    {
        Staff.AuditPages.Add(new AuditPageDto(
            [FakeStaffApiClient.Entry(3, "elevator.service-action", "requested"), FakeStaffApiClient.Entry(2, outcome: "failed", actor: null), FakeStaffApiClient.Entry(1, actor: null, anonymised: true)],
            null));

        var panel = Render<AuditTrailPanel>();

        panel.WaitForAssertion(() => panel.FindAll("[data-testid=audit-entries] tbody tr").ShouldNotBeEmpty());
        panel.FindAll("[data-testid=audit-entries] tbody tr").Select(row => row.GetAttribute("data-sequence")).ShouldBe(["3", "2", "1"]);
        panel.Find("tr[data-sequence='3']").TextContent.ShouldContain("admin@elevator.test");
        panel.Find("tr[data-sequence='2']").TextContent.ShouldContain("unknown");
        panel.Find("tr[data-sequence='1']").TextContent.ShouldContain("anonymised");
        panel.FindAll("[data-testid=audit-more]").ShouldBeEmpty();
    }

    [Fact]
    public void LoadMore_AsksForTheEntriesBeforeTheLastOne()
    {
        Staff.AuditPages.Add(new AuditPageDto([FakeStaffApiClient.Entry(4), FakeStaffApiClient.Entry(3)], 3));
        Staff.AuditPages.Add(new AuditPageDto([FakeStaffApiClient.Entry(2), FakeStaffApiClient.Entry(1)], null));
        var panel = Render<AuditTrailPanel>();

        panel.WaitForElement("[data-testid=audit-more]").Click();

        panel.WaitForAssertion(() => panel.FindAll("[data-testid=audit-entries] tbody tr").Count.ShouldBe(4));
        Staff.AuditPageRequests.ShouldBe([null, 3]);
    }

    [Fact]
    public void Verify_AnIntactChain_SaysHowManyEntriesWereChecked()
    {
        var panel = Render<AuditTrailPanel>();

        panel.Find("[data-testid=audit-verify]").Click();

        panel.WaitForAssertion(() => panel.Find("[data-testid=audit-verification]"));
        panel.Find("[data-testid=audit-verification]").GetAttribute("data-intact").ShouldBe("true");
        panel.Find("[data-testid=audit-verification]").TextContent.ShouldContain("Intact: 4 entries verified.");
    }

    [Fact]
    public void Verify_ABrokenChain_SaysWhereAndWhy()
    {
        Staff.Verification = StaffResult.Ok(new AuditVerificationDto(false, 2, null, null, 2, "EntryHashMismatch", TestData.Noon));
        var panel = Render<AuditTrailPanel>();

        panel.Find("[data-testid=audit-verify]").Click();

        panel.WaitForAssertion(() => panel.Find("[data-testid=audit-verification]").TextContent
            .ShouldContain("Broken at entry 2 (EntryHashMismatch). The trail was changed outside the application."));
    }

    [Fact]
    public void ASessionThatEnded_IsExplained()
    {
        Staff.Verification = StaffResult.Fail<AuditVerificationDto>(StaffFailure.SessionExpired);
        var panel = Render<AuditTrailPanel>();

        panel.Find("[data-testid=audit-verify]").Click();

        panel.WaitForAssertion(() => panel.Find("[data-testid=audit-message]").TextContent.ShouldContain("Your session ended."));
    }
}
