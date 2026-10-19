using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Features.Passenger;
using ElevatorSystem.Web.Services;

namespace ElevatorSystem.Web.Tests.Features;

public sealed class PassengerViewTests : WebTestContext
{
    [Fact]
    public void Load_FetchesTheBuildingAndStartsTheHub()
    {
        var view = Render<PassengerView>();

        Api.ConfigurationRequests.ShouldBe(1);
        Hub.StartCalls.ShouldBe(1);
        view.FindAll(".landing").Count.ShouldBe(10);
    }

    [Fact]
    public void Load_ShowsTheStatusFromTheApiBeforeTheHubConnects()
    {
        Api.Status = TestData.Status(floor: 7, version: 3);

        var view = Render<PassengerView>();

        view.Find("[data-testid=status-floor]").TextContent.ShouldContain("7");
    }

    [Fact]
    public void Load_ConfigurationUnavailable_OffersToTryAgain()
    {
        Api.ConfigurationFailure = new HttpRequestException("refused");
        var view = Render<PassengerView>();
        view.Find("[data-testid=load-failed]").TextContent.ShouldContain("The elevator can't be reached right now.");

        Api.ConfigurationFailure = null;
        view.FindAll("button").Single(button => button.TextContent.Contains("Try again", StringComparison.Ordinal)).Click();

        view.FindAll(".landing").Count.ShouldBe(10);
        view.FindAll("[data-testid=load-failed]").ShouldBeEmpty();
    }

    [Fact]
    public void HallButton_Pressed_SendsTheHallCall()
    {
        var view = Render<PassengerView>();

        view.Find(".hall-button[data-floor='3'][data-direction='Up']").Click();

        Api.HallCalls.ShouldBe([new HallCallRequest(3, CallDirection.Up)]);
    }

    [Fact]
    public void CarButton_Pressed_SendsTheCarCall()
    {
        var view = Render<PassengerView>();

        view.Find(".car-button[data-floor='8']").Click();

        Api.CarCalls.ShouldBe([8]);
    }

    [Fact]
    public void HallButton_PressedAgainWhileTheRequestIsOnItsWay_SendsOnlyOnce()
    {
        Api.PendingAnswer = new TaskCompletionSource<RequestOutcome>();
        var view = Render<PassengerView>();
        var button = view.Find(".hall-button[data-floor='3'][data-direction='Up']");

        button.Click();
        view.Find(".hall-button[data-floor='3'][data-direction='Up']").Click();

        Api.HallCalls.Count.ShouldBe(1);
    }

    [Fact]
    public void Lamp_LightsWhenTheServerReportsTheCall()
    {
        var view = Render<PassengerView>();

        Store.ApplyStatus(TestData.Status(version: 10, pending: TestData.HallCall(5, CallDirection.Down)));

        view.WaitForAssertion(() =>
            view.Find(".hall-button[data-floor='5'][data-direction='Down']").GetAttribute("aria-pressed").ShouldBe("true"));
    }

    [Fact]
    public void Lamp_GoesOutWhenTheServerReportsTheCallServed()
    {
        var view = Render<PassengerView>();
        Store.ApplyStatus(TestData.Status(version: 10, pending: TestData.CarCall(5)));

        Store.ApplyStatus(TestData.Status(floor: 5, status: ElevatorStatus.DoorOpen, version: 11));

        view.WaitForAssertion(() => view.Find(".car-button[data-floor='5']").GetAttribute("aria-pressed").ShouldBe("false"));
    }

    [Theory]
    [InlineData(RejectionReason.OutOfService, "The elevator is out of service right now. Please use the stairs or try again later.")]
    [InlineData(RejectionReason.Busy, "The elevator is handling many calls. Please try again in a moment.")]
    [InlineData(RejectionReason.InvalidFloor, "That floor isn't served by this elevator.")]
    [InlineData(RejectionReason.InvalidDirection, "That direction isn't available from this floor.")]
    public void Refusal_ShowsTheReasonAsAnAlert(RejectionReason reason, string message)
    {
        Api.NextOutcome = RequestOutcome.Refused(reason, message);
        var view = Render<PassengerView>();

        view.Find(".hall-button[data-floor='3'][data-direction='Up']").Click();

        var alert = view.Find("[data-testid=refusal]");
        alert.GetAttribute("role").ShouldBe("alert");
        alert.GetAttribute("data-reason").ShouldBe(reason.ToString());
        alert.TextContent.ShouldContain("Request not accepted.");
        alert.TextContent.ShouldContain(message);
    }

    [Fact]
    public void Refusal_ClearsWhenTheNextRequestIsAccepted()
    {
        Api.NextOutcome = RequestOutcome.Refused(RejectionReason.Busy, "busy");
        var view = Render<PassengerView>();
        view.Find(".car-button[data-floor='2']").Click();

        Api.NextOutcome = RequestOutcome.Accepted(Guid.NewGuid());
        view.Find(".car-button[data-floor='4']").Click();

        view.FindAll("[data-testid=refusal]").ShouldBeEmpty();
    }

    [Fact]
    public void Refusal_CanBeDismissed()
    {
        Api.NextOutcome = RequestOutcome.Refused(RejectionReason.Busy, "busy");
        var view = Render<PassengerView>();
        view.Find(".car-button[data-floor='2']").Click();

        view.Find("[data-testid=refusal] button").Click();

        view.FindAll("[data-testid=refusal]").ShouldBeEmpty();
    }

    [Fact]
    public void ConnectionLost_ShowsTheReconnectingBannerOverTheLastKnownState()
    {
        var view = Render<PassengerView>();
        Store.SetConnection(ConnectionState.Connected);
        Store.ApplyStatus(TestData.Status(floor: 6, version: 20));

        Store.SetConnection(ConnectionState.Reconnecting);

        view.WaitForAssertion(() => view.Find("[data-testid=connection-banner]").TextContent.ShouldContain("Reconnecting…"));
        view.Find("[data-testid=status-floor]").TextContent.ShouldContain("6");
    }

    [Fact]
    public void ConnectionRestored_HidesTheBanner()
    {
        var view = Render<PassengerView>();
        Store.SetConnection(ConnectionState.Reconnecting);

        Store.SetConnection(ConnectionState.Connected);

        view.WaitForAssertion(() => view.FindAll("[data-testid=connection-banner]").ShouldBeEmpty());
    }

    [Fact]
    public void Activity_FromTheHub_AppearsInTheFeed()
    {
        var view = Render<PassengerView>();

        Store.AddActivity(TestData.DoorsOpened(4));

        view.WaitForAssertion(() => view.Find(".activity__text").TextContent.ShouldBe("Doors opened at floor 4."));
    }

    [Fact]
    public void EveryButton_IsARealButtonWithAName()
    {
        var view = Render<PassengerView>();

        var buttons = view.FindAll("button");

        buttons.Count.ShouldBe(18 + 10, "18 hall buttons and 10 car buttons");
        buttons.ShouldAllBe(button =>
            !string.IsNullOrWhiteSpace(button.GetAttribute("aria-label")) || !string.IsNullOrWhiteSpace(button.TextContent));
    }

    [Fact]
    public void Sections_AreNamedByTheirHeadings()
    {
        var view = Render<PassengerView>();

        view.FindAll("section[aria-labelledby]").Select(section => view.Find($"#{section.GetAttribute("aria-labelledby")}").TextContent)
            .ShouldBe(["Floors", "Inside the car", "Request queue", "Recent activity"]);
    }
}
