using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Features.Admin;
using ElevatorSystem.Web.Services;

namespace ElevatorSystem.Web.Tests.Features;

/// <summary>The admin view's steps: sign-in, two-factor registration, then the dashboard (ADR-0022).</summary>
public sealed class AdminViewTests : WebTestContext
{
    [Fact]
    public void NobodySignedIn_ShowsTheSignInForm()
    {
        var view = Render<AdminView>();

        view.WaitForAssertion(() => view.Find("#sign-in-heading").TextContent.ShouldBe("Staff sign-in"));
        view.FindAll("[data-testid=code]").ShouldBeEmpty("the code is asked for only after a correct password");
    }

    [Fact]
    public void SignIn_WrongPassword_ExplainsWithoutSayingWhichPartWasWrong_AndClearsThePassword()
    {
        Staff.SignInAnswers.Enqueue(StaffResult.Fail<StaffSessionDto>(StaffFailure.InvalidCredentials));
        var view = Render<AdminView>();

        SignIn(view, "admin@elevator.test", "wrong password");

        view.WaitForAssertion(() => view.Find("[data-testid=sign-in-message]").TextContent.ShouldContain("The e-mail or password is not correct"));
        view.Find("input[data-testid=password]").GetAttribute("value").ShouldBeNullOrEmpty();
    }

    [Fact]
    public void SignIn_WhenTheServerAsksForTheSecondFactor_AsksForTheCode_AndSendsItWithThePassword()
    {
        Staff.SignInAnswers.Enqueue(StaffResult.Fail<StaffSessionDto>(StaffFailure.TwoFactorRequired));
        Staff.SignInAnswers.Enqueue(StaffResult.Ok(FakeStaffApiClient.Admin));
        var view = Render<AdminView>();

        SignIn(view, "admin@elevator.test", "correct horse battery staple");
        view.WaitForAssertion(() => view.Find("input[data-testid=code]"));
        view.Find("input[data-testid=code]").Change("123 456");
        view.Find("form").Submit();

        view.WaitForAssertion(() => view.Find("[data-testid=account]").TextContent.ShouldContain("Signed in as admin@elevator.test"));
        Staff.SignIns[^1].ShouldBe(new SignInRequest("admin@elevator.test", "correct horse battery staple", "123 456", null));
        Hub.RestartCalls.ShouldBe(1, "the hub reconnects under the new session to receive staff-only counts");
    }

    [Fact]
    public void SignIn_WithARecoveryCode_SendsItAsARecoveryCode()
    {
        Staff.SignInAnswers.Enqueue(StaffResult.Fail<StaffSessionDto>(StaffFailure.TwoFactorRequired));
        Staff.SignInAnswers.Enqueue(StaffResult.Ok(FakeStaffApiClient.Admin));
        var view = Render<AdminView>();
        SignIn(view, "admin@elevator.test", "correct horse battery staple");

        view.WaitForElement("[data-testid=toggle-recovery]").Click();
        view.Find("input[data-testid=code]").Change("AAAAA-11111");
        view.Find("form").Submit();

        view.WaitForAssertion(() => Staff.SignIns[^1].RecoveryCode.ShouldBe("AAAAA-11111"));
        Staff.SignIns[^1].TwoFactorCode.ShouldBeNull();
    }

    [Fact]
    public void PasswordOnlySession_RegistersTheAuthenticator_ShowsTheRecoveryCodesOnce_ThenTheDashboard()
    {
        Staff.Session = FakeStaffApiClient.PasswordOnly;
        var view = Render<AdminView>();

        view.WaitForAssertion(() => view.Find("[data-testid=qr]").GetAttribute("src").ShouldStartWith("data:image/png;base64,"));
        view.Find("[data-testid=shared-key]").TextContent.ShouldBe("jbsw y3dp ehpk 3pxp jbsw y3dp ehpk 3pxp");
        view.FindAll("[data-testid=occupancy-unavailable]").ShouldBeEmpty("nothing of the dashboard shows before the second factor");

        view.Find("input[data-testid=code]").Change("654321");
        view.Find("form").Submit();
        view.WaitForAssertion(() => view.Find("[data-testid=recovery-codes]").TextContent.ShouldContain("AAAAA-11111"));
        view.Find("[data-testid=codes-saved]").Click();

        view.WaitForAssertion(() => view.Find("#service-heading"));
        Staff.EnableCodes.ShouldBe(["654321"]);
        view.FindAll("[data-testid=recovery-codes]").ShouldBeEmpty();
    }

    [Fact]
    public void Enrolment_WithAWrongCode_SaysSo_AndStaysOnTheStep()
    {
        Staff.Session = FakeStaffApiClient.PasswordOnly;
        Staff.Enable = StaffResult.Fail<RecoveryCodesDto>(StaffFailure.InvalidTwoFactorCode);
        var view = Render<AdminView>();

        view.WaitForElement("input[data-testid=code]").Change("000000");
        view.Find("form").Submit();

        view.WaitForAssertion(() => view.Find("[data-testid=enrol-message]").TextContent.ShouldContain("That code is not valid"));
        view.Find("[data-testid=qr]");
    }

    [Fact]
    public void Dashboard_ShowsTheCarLoadAndThePeoplePerFloor()
    {
        Staff.Session = FakeStaffApiClient.Auditor;
        Staff.Occupancy = FakeStaffApiClient.OccupancyOf(inCar: 3, (1, 2, 5), (7, 1, 0));
        var view = Render<AdminView>();

        view.WaitForAssertion(() => view.Find("[data-testid=car-load]").TextContent.ShouldBe("3 of 8"));
        view.Find("[data-testid=delivered]").TextContent.ShouldBe("12");
        view.Find("[data-testid=average-wait]").TextContent.Trim().ShouldBe("7.5 s");
        var floors = view.FindAll("[data-testid=floors] tbody tr");
        floors.Select(row => row.GetAttribute("data-floor")).ShouldBe([.. Enumerable.Range(1, 10).Reverse().Select(floor => floor.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
        view.Find("tr[data-floor='1']").TextContent.ShouldContain("2");
        view.Find("[data-testid=totals]").TextContent.ShouldContain("3");
        view.Find("[data-testid=totals]").TextContent.ShouldContain("5");
    }

    [Fact]
    public void Dashboard_FollowsTheCountsTheHubPushes()
    {
        Staff.Session = FakeStaffApiClient.Admin;
        var view = Render<AdminView>();
        view.WaitForAssertion(() => view.Find("[data-testid=occupancy-unavailable]"));

        Store.ApplyOccupancy(FakeStaffApiClient.OccupancyOf(inCar: 8));

        view.WaitForAssertion(() => view.Find("[data-testid=car-load]").TextContent.ShouldBe("8 of 8"));
    }

    [Fact]
    public void TestAccount_GoesStraightToTheDashboard_AndSaysItIsATestAccount()
    {
        Staff.Session = new StaffSessionDto("test.admin@elevator.test", [StaffRole.Admin], TwoFactorEnabled: false, TwoFactorVerified: false, TestAccount: true);
        var view = Render<AdminView>();

        view.WaitForAssertion(() => view.Find("#service-heading"));
        view.Find("[data-testid=test-account]").TextContent.ShouldContain("Test account: signed in without a second factor.");
        view.FindAll("[data-testid=shared-key]").ShouldBeEmpty("a test account is not asked to register an authenticator");
    }

    [Fact]
    public void Auditor_SeesNoServiceControls()
    {
        Staff.Session = FakeStaffApiClient.Auditor;
        var view = Render<AdminView>();

        view.WaitForAssertion(() => view.Find("#audit-heading"));
        view.FindAll("#service-heading").ShouldBeEmpty();
    }

    [Fact]
    public void SignOut_ReturnsToTheSignInForm_AndForgetsTheCounts()
    {
        Staff.Session = FakeStaffApiClient.Admin;
        Store.ApplyOccupancy(FakeStaffApiClient.OccupancyOf(inCar: 2));
        var view = Render<AdminView>();

        view.WaitForElement("[data-testid=sign-out]").Click();

        view.WaitForAssertion(() => view.Find("#sign-in-heading"));
        Staff.SignOuts.ShouldBe(1);
        Store.Occupancy.ShouldBeNull();
        Hub.RestartCalls.ShouldBe(1);
    }

    [Fact]
    [UseCulture("pt-BR")]
    public void SignIn_InPortuguese()
    {
        var view = Render<AdminView>();

        view.WaitForAssertion(() => view.Find("#sign-in-heading").TextContent.ShouldBe("Acesso da equipe"));
        view.Find("[data-testid=sign-in]").TextContent.Trim().ShouldBe("Entrar");
    }

    private static void SignIn(IRenderedComponent<AdminView> view, string email, string password)
    {
        view.WaitForElement("input[data-testid=email]").Change(email);
        view.Find("input[data-testid=password]").Change(password);
        view.Find("form").Submit();
    }
}
