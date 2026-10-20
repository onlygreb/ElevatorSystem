using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Features.Admin;
using ElevatorSystem.Web.Services;

namespace ElevatorSystem.Web.Tests.Features;

/// <summary>Service actions need a confirmation, make sense only in the right state, and retry safely.</summary>
public sealed class ServiceControlsTests : WebTestContext
{
    [Fact]
    public void InService_OffersMaintenanceAndEmergencyStop_NotReturnToService()
    {
        var controls = Render<ServiceControls>(parameters => parameters.Add(component => component.Status, TestData.Status()));

        controls.Find("[data-testid=action-StartMaintenance]").HasAttribute("disabled").ShouldBeFalse();
        controls.Find("[data-testid=action-EmergencyStop]").HasAttribute("disabled").ShouldBeFalse();
        controls.Find("[data-testid=action-ReturnToService]").HasAttribute("disabled").ShouldBeTrue();
    }

    [Fact]
    public void OutOfService_OffersOnlyReturnToService()
    {
        var controls = Render<ServiceControls>(parameters => parameters.Add(component => component.Status, OutOfService()));

        controls.Find("[data-testid=action-StartMaintenance]").HasAttribute("disabled").ShouldBeTrue();
        controls.Find("[data-testid=action-EmergencyStop]").HasAttribute("disabled").ShouldBeTrue();
        controls.Find("[data-testid=action-ReturnToService]").HasAttribute("disabled").ShouldBeFalse();
    }

    [Fact]
    public void SchedulingSwitch_InService_IsDisabled_AndSaysWhy()
    {
        var controls = Render<ServiceControls>(parameters => parameters.Add(component => component.Status, TestData.Status() with { Scheduling = Scheduling.Fifo }));

        controls.Find("[data-testid=scheduling-Fifo]").GetAttribute("aria-checked").ShouldBe("true");
        controls.Find("[data-testid=scheduling-Look]").HasAttribute("disabled").ShouldBeTrue();
        controls.Find("#scheduling-hint").TextContent.ShouldBe("Take the car out of service to switch how it schedules its calls.");
    }

    [Fact]
    public void SchedulingSwitch_OutOfService_SendsTheChoice()
    {
        var controls = Render<ServiceControls>(parameters => parameters.Add(component => component.Status, OutOfService() with { Scheduling = Scheduling.Fifo }));

        controls.Find("[data-testid=scheduling-Look]").Click();

        Staff.SchedulingChanges.ShouldBe([Scheduling.Look]);
        controls.Find("[data-testid=action-message]").TextContent.ShouldBe("Switching to LOOK.");
    }

    [Fact]
    public void SchedulingSwitch_RefusedBecauseTheCarIsBackInService_SaysSo()
    {
        Staff.SchedulingAnswer = StaffResult.Fail<SchedulingAcceptedResponse>(StaffFailure.Conflict);
        var controls = Render<ServiceControls>(parameters => parameters.Add(component => component.Status, OutOfService() with { Scheduling = Scheduling.Fifo }));

        controls.Find("[data-testid=scheduling-Look]").Click();

        controls.Find("[data-testid=action-message]").TextContent.ShouldBe("Take the car out of service to switch how it schedules its calls.");
    }

    [Fact]
    public void EmergencyStop_IsSentOnlyAfterConfirming()
    {
        var controls = Render<ServiceControls>(parameters => parameters.Add(component => component.Status, TestData.Status()));

        controls.Find("[data-testid=action-EmergencyStop]").Click();

        controls.Find("[data-testid=confirm]").TextContent.ShouldContain("Confirm: Emergency stop?");
        controls.Find("[data-testid=confirm]").TextContent.ShouldContain("every pending call is cancelled");
        Staff.ServiceActions.ShouldBeEmpty();

        controls.Find("[data-testid=confirm-yes]").Click();

        controls.WaitForAssertion(() => Staff.ServiceActions.Select(sent => sent.Action).ShouldBe([ServiceAction.EmergencyStop]));
        controls.Find("[data-testid=action-message]").TextContent.ShouldContain("Request sent: Emergency stop.");
    }

    [Fact]
    public void Cancelling_SendsNothing()
    {
        var controls = Render<ServiceControls>(parameters => parameters.Add(component => component.Status, TestData.Status()));

        controls.Find("[data-testid=action-StartMaintenance]").Click();
        controls.Find("[data-testid=confirm-no]").Click();

        Staff.ServiceActions.ShouldBeEmpty();
        controls.FindAll("[data-testid=confirm]").ShouldBeEmpty();
    }

    [Fact]
    public void ARefusedAction_KeepsTheConfirmation_AndTheRetryReusesItsKey()
    {
        Staff.ServiceAnswers.Enqueue(StaffResult.Fail<ServiceActionAcceptedResponse>(StaffFailure.Unavailable));
        var controls = Render<ServiceControls>(parameters => parameters.Add(component => component.Status, OutOfService()));

        controls.Find("[data-testid=action-ReturnToService]").Click();
        controls.Find("[data-testid=confirm-yes]").Click();
        controls.WaitForAssertion(() => controls.Find("[data-testid=action-message]").TextContent.ShouldContain("cannot be reached"));
        controls.Find("[data-testid=confirm-yes]").Click();

        controls.WaitForAssertion(() => Staff.ServiceActions.Count.ShouldBe(2));
        Staff.ServiceActions[1].Key.ShouldBe(Staff.ServiceActions[0].Key, "a retry must not act twice (Idempotency-Key)");
        controls.FindAll("[data-testid=confirm]").ShouldBeEmpty();
    }

    [Fact]
    public void ANewAction_GetsANewKey()
    {
        var controls = Render<ServiceControls>(parameters => parameters.Add(component => component.Status, TestData.Status()));

        controls.Find("[data-testid=action-StartMaintenance]").Click();
        controls.Find("[data-testid=confirm-yes]").Click();
        controls.WaitForAssertion(() => Staff.ServiceActions.Count.ShouldBe(1));
        controls.Find("[data-testid=action-StartMaintenance]").Click();
        controls.Find("[data-testid=confirm-yes]").Click();

        controls.WaitForAssertion(() => Staff.ServiceActions.Count.ShouldBe(2));
        Staff.ServiceActions[1].Key.ShouldNotBe(Staff.ServiceActions[0].Key);
    }

    private static ElevatorStatusDto OutOfService() =>
        TestData.Status() with { Status = ElevatorStatus.OutOfService, OutOfServiceCause = OutOfServiceCause.Maintenance };
}
