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
        view.FindAll(".storey").Count.ShouldBe(10);
    }

    [Fact]
    public void Load_ShowsTheStatusFromTheApiBeforeTheHubConnects()
    {
        Api.Status = TestData.Status(floor: 7, version: 3);

        var view = Render<PassengerView>();

        view.Find("[data-testid=status-floor]").TextContent.ShouldContain("7");
        view.Find("[data-testid=car]").GetAttribute("data-floor").ShouldBe("7");
    }

    [Fact]
    public void Load_ConfigurationUnavailable_OffersToTryAgain()
    {
        Api.ConfigurationFailure = new HttpRequestException("refused");
        var view = Render<PassengerView>();
        view.Find("[data-testid=load-failed]").TextContent.ShouldContain("The elevator can't be reached right now.");

        Api.ConfigurationFailure = null;
        view.FindAll("button").Single(button => button.TextContent.Contains("Try again", StringComparison.Ordinal)).Click();

        view.FindAll(".storey").Count.ShouldBe(10);
        view.FindAll("[data-testid=load-failed]").ShouldBeEmpty();
    }

    [Fact]
    public void Outside_OffersOnlyToEnterTheBuilding()
    {
        var view = Render<PassengerView>();

        view.Find("[data-testid=journey-where]").TextContent.ShouldBe("You are outside the building");
        view.Find("[data-testid=arrive]").TextContent.ShouldContain("Enter the building");
        view.FindAll(".car-button").ShouldBeEmpty();
        view.FindAll("[data-testid=you-on-floor]").ShouldBeEmpty();
    }

    [Fact]
    public void Arrive_PutsYouOnTheLowestFloor()
    {
        var view = Render<PassengerView>();

        view.Find("[data-testid=arrive]").Click();

        Passengers.Joins.ShouldBe(1);
        view.Find("[data-testid=journey-where]").TextContent.ShouldBe("You are on floor 1");
        view.Find(".storey[data-floor='1'] [data-testid=you-on-floor]").TextContent.ShouldBe("You");
    }

    [Fact]
    public void Arrive_RemembersThePassengerForTheNextVisit()
    {
        var view = Render<PassengerView>();

        view.Find("[data-testid=arrive]").Click();

        JSInterop.VerifyInvoke("localStorage.setItem").Arguments.ShouldBe([PassengerJourney.StorageKey, Passengers.PassengerId.ToString("D")]);
    }

    [Fact]
    public void OnTheLowestFloor_YouCanOnlyCallToGoUp()
    {
        Api.Status = TestData.Status(floor: 6);
        var view = Arrived();

        view.FindAll("[data-testid=call-down]").ShouldBeEmpty();
        view.Find("[data-testid=call-up]").Click();

        Passengers.Steps.ShouldBe(["call Up"]);
    }

    [Fact]
    public void OnAHigherFloor_YouCanCallEitherWay()
    {
        Api.Status = TestData.Status(floor: 1);
        var view = Arrived();
        Mine(ElevatorActivityKind.PassengerLeft, 5);

        view.WaitForAssertion(() => view.Find("[data-testid=call-down]").Click());

        view.Find("[data-testid=call-up]").ShouldNotBeNull();
        Passengers.Steps.ShouldBe(["call Down"]);
    }

    [Fact]
    public void Called_ShowsThatYouAreWaiting()
    {
        Api.Status = TestData.Status(floor: 6, pending: TestData.HallCall(1, CallDirection.Up));
        var view = Arrived();

        Mine(ElevatorActivityKind.PassengerCalled, 1, CallDirection.Up);

        view.WaitForAssertion(() => view.Find("[data-testid=waiting]").TextContent.ShouldContain("Waiting for the elevator to go up"));
        view.FindAll("[data-testid=call-again]").ShouldBeEmpty("the call is still on its way");
    }

    [Fact]
    public void Called_ButTheCarLeftWithoutYou_OffersToCallAgain()
    {
        Api.Status = TestData.Status(floor: 6);
        var view = Arrived();
        Mine(ElevatorActivityKind.PassengerCalled, 1, CallDirection.Up);

        view.WaitForAssertion(() => view.Find("[data-testid=call-again]").Click());

        Passengers.Steps.ShouldBe(["call Up"]);
    }

    [Fact]
    public void DoorOpenOnYourFloor_OffersToEnterWithTheCountdown()
    {
        var view = Arrived();

        Store.ApplyStatus(TestData.Status(floor: 1, status: ElevatorStatus.DoorOpen, version: 10) with { DoorsCloseAt = TestData.Noon.AddSeconds(2) });

        view.WaitForAssertion(() => view.Find("[data-testid=enter]").TextContent.ShouldContain("Enter the elevator"));
        view.Find(".journey [data-testid=door-countdown]").TextContent.ShouldContain("Doors close in 2 s");
        view.Find("[data-testid=enter]").Click();
        Passengers.Steps.ShouldBe(["board"]);
    }

    [Fact]
    public void DoorHeldOpenOnYourFloor_OffersToEnterWithoutACountdown()
    {
        var view = Arrived();

        Store.ApplyStatus(TestData.Status(floor: 1, status: ElevatorStatus.DoorOpen, version: 10));

        view.WaitForAssertion(() => view.Find("[data-testid=enter]").ShouldNotBeNull());
        view.Find(".journey [data-testid=door-held]").TextContent.ShouldContain("Doors open, waiting for a call");
    }

    [Fact]
    public void DoorOpenOnAnotherFloor_DoesNotOfferToEnter()
    {
        var view = Arrived();

        Store.ApplyStatus(TestData.Status(floor: 4, status: ElevatorStatus.DoorOpen, version: 10));

        view.WaitForAssertion(() => view.Find("[data-testid=car]").GetAttribute("data-floor").ShouldBe("4"));
        view.FindAll("[data-testid=enter]").ShouldBeEmpty();
    }

    [Fact]
    public void Boarded_ShowsTheCarPanelAndYouInTheCar()
    {
        var view = Arrived();

        Mine(ElevatorActivityKind.PassengerBoarded, 1);

        view.WaitForAssertion(() => view.FindAll(".car-button").Count.ShouldBe(10));
        view.Find("[data-testid=journey-where]").TextContent.ShouldBe("You are in the elevator, at floor 1");
        view.Find("[data-testid=you-in-car]").ShouldNotBeNull();
        view.FindAll("[data-testid=you-on-floor]").ShouldBeEmpty();
    }

    [Fact]
    public void InTheCar_ChoosingAFloorSendsIt()
    {
        var view = InTheCar();

        view.Find(".car-button[data-floor='8']").Click();

        Passengers.Steps.ShouldBe(["floor 8"]);
    }

    [Fact]
    public void InTheCar_TheChosenFloorIsShownAndLit()
    {
        var view = InTheCar();

        Mine(ElevatorActivityKind.PassengerChoseFloor, 8);
        Store.ApplyStatus(TestData.Status(floor: 1, version: 20, pending: TestData.CarCall(8)));

        view.WaitForAssertion(() => view.Find(".journey__hint").TextContent.ShouldBe("Going to floor 8"));
        view.Find(".car-button[data-floor='8']").GetAttribute("aria-pressed").ShouldBe("true");
    }

    [Fact]
    public void InTheCar_LeavingWaitsForTheDoorsToOpen()
    {
        var view = InTheCar();
        Store.ApplyStatus(TestData.Status(floor: 3, status: ElevatorStatus.MovingUp, version: 20));

        view.WaitForAssertion(() => view.Find("[data-testid=leave]").HasAttribute("disabled").ShouldBeTrue());

        Store.ApplyStatus(TestData.Status(floor: 4, status: ElevatorStatus.DoorOpen, version: 21) with { DoorsCloseAt = TestData.Noon.AddSeconds(4) });
        view.WaitForAssertion(() => view.Find("[data-testid=leave]").HasAttribute("disabled").ShouldBeFalse());
        view.Find("[data-testid=leave]").Click();

        Passengers.Steps.ShouldBe(["leave"]);
    }

    [Fact]
    public void InTheCar_YouRideAlongUntilYouLeave()
    {
        var view = InTheCar();

        Store.ApplyStatus(TestData.Status(floor: 6, status: ElevatorStatus.MovingUp, version: 20));

        view.WaitForAssertion(() => view.Find("[data-testid=journey-where]").TextContent.ShouldBe("You are in the elevator, at floor 6"));
    }

    [Fact]
    public void Left_PutsYouOnTheFloorWhereYouWalkedOut()
    {
        var view = InTheCar();

        Mine(ElevatorActivityKind.PassengerLeft, 7);

        view.WaitForAssertion(() => view.Find("[data-testid=journey-where]").TextContent.ShouldBe("You are on floor 7"));
        view.Find(".storey[data-floor='7'] [data-testid=you-on-floor]").ShouldNotBeNull();
        view.FindAll(".car-button").ShouldBeEmpty();
    }

    [Fact]
    public void StepPressedAgainWhileOnItsWay_IsSentOnlyOnce()
    {
        var view = Arrived();
        Passengers.PendingAnswer = new TaskCompletionSource<RequestOutcome>();

        view.Find("[data-testid=call-up]").Click();

        view.Find("[data-testid=call-up]").HasAttribute("disabled").ShouldBeTrue();
        Passengers.Steps.Count.ShouldBe(1);
    }

    [Fact]
    public void RefusedStep_IsExplainedAsAnAlert()
    {
        var view = Arrived();

        Mine(ElevatorActivityKind.PassengerRefused, 1, refusal: PassengerRefusalReason.CarFull);

        view.WaitForAssertion(() =>
        {
            var message = view.Find("[data-testid=journey-message]");
            message.GetAttribute("role").ShouldBe("alert");
            message.TextContent.ShouldContain("The elevator is full.");
        });
    }

    [Fact]
    public void RefusedStep_ClearsWhenTheNextStepIsSent()
    {
        var view = Arrived();
        Mine(ElevatorActivityKind.PassengerRefused, 1, refusal: PassengerRefusalReason.DoorNotOpenHere);
        view.WaitForAssertion(() => view.Find("[data-testid=journey-message]").ShouldNotBeNull());

        view.Find("[data-testid=call-up]").Click();

        view.FindAll("[data-testid=journey-message]").ShouldBeEmpty();
    }

    [Fact]
    public void StepThatNeverReachedTheElevator_ShowsWhy()
    {
        var view = Arrived();
        Passengers.NextOutcome = RequestOutcome.Refused(null, "Too many requests.");

        view.Find("[data-testid=call-up]").Click();

        view.Find("[data-testid=journey-message]").TextContent.ShouldContain("Too many requests.");
    }

    [Fact]
    public void ForgottenPassenger_StartsOutsideAgain()
    {
        var view = Arrived();

        Mine(ElevatorActivityKind.PassengerRefused, 1, refusal: PassengerRefusalReason.UnknownPassenger);

        view.WaitForAssertion(() => view.Find("[data-testid=arrive]").ShouldNotBeNull());
        view.Find("[data-testid=journey-message]").TextContent.ShouldContain("The building lost track of you.");
    }

    [Fact]
    public void Reload_PicksUpTheJourneyTheBuildingStillKnows()
    {
        var id = Passengers.PassengerId;
        JSInterop.Setup<string?>("localStorage.getItem", PassengerJourney.StorageKey).SetResult(id.ToString("D"));
        Passengers.Known = new PassengerDto(id, 3, InCar: true, Waiting: null, Destination: 9, Simulated: false);
        Api.Status = TestData.Status(floor: 3);

        var view = Render<PassengerView>();

        view.WaitForAssertion(() => view.Find("[data-testid=journey-where]").TextContent.ShouldBe("You are in the elevator, at floor 3"));
        Passengers.Joins.ShouldBe(0);
    }

    [Fact]
    public void Reload_OfAPassengerTheBuildingForgot_StartsOutside()
    {
        JSInterop.Setup<string?>("localStorage.getItem", PassengerJourney.StorageKey).SetResult(Guid.NewGuid().ToString("D"));

        var view = Render<PassengerView>();

        view.Find("[data-testid=arrive]").ShouldNotBeNull();
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
    public void Activity_OfYourOwn_IsMarkedAsYours()
    {
        var view = Arrived();

        Mine(ElevatorActivityKind.PassengerCalled, 1, CallDirection.Up);

        view.WaitForAssertion(() => view.Find(".activity__item").ClassList.ShouldContain("activity__item--mine"));
        view.Find(".activity__you").TextContent.ShouldBe("You");
    }

    [Fact]
    public void EveryButton_IsARealButtonWithAName()
    {
        var view = InTheCar();

        var buttons = view.FindAll("button");

        buttons.Count.ShouldBe(10 + 1, "10 car buttons and the leave button");
        buttons.ShouldAllBe(button =>
            !string.IsNullOrWhiteSpace(button.GetAttribute("aria-label")) || !string.IsNullOrWhiteSpace(button.TextContent));
    }

    [Fact]
    public void Sections_AreNamedByTheirHeadings()
    {
        var view = Render<PassengerView>();

        view.FindAll("section[aria-labelledby]").Select(section => view.Find($"#{section.GetAttribute("aria-labelledby")}").TextContent)
            .ShouldBe(["Your journey", "Request queue", "Recent activity", "Floors"]);
    }

    private IRenderedComponent<PassengerView> Arrived()
    {
        var view = Render<PassengerView>();
        view.Find("[data-testid=arrive]").Click();
        return view;
    }

    private IRenderedComponent<PassengerView> InTheCar()
    {
        var view = Arrived();
        Mine(ElevatorActivityKind.PassengerBoarded, 1);
        view.WaitForAssertion(() => view.FindAll(".car-button").Count.ShouldBe(10));
        return view;
    }

    /// <summary>Plays the server reporting one of this browser's own passenger steps.</summary>
    private void Mine(ElevatorActivityKind kind, int floor, CallDirection? direction = null, PassengerRefusalReason? refusal = null) =>
        Store.AddActivity(new ElevatorActivityDto(TestData.Noon, kind, floor, Direction: direction, PassengerId: Passengers.PassengerId,
            Simulated: false, PassengerRefusal: refusal));
}
