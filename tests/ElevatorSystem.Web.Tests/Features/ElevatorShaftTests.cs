using AngleSharp.Dom;
using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Features.Passenger;

namespace ElevatorSystem.Web.Tests.Features;

public sealed class ElevatorShaftTests : WebTestContext
{
    [Fact]
    public void Landings_AreListedFromTheTopFloorDown()
    {
        var shaft = RenderShaft(TestData.Status());

        shaft.FindAll(".landing").Select(landing => landing.GetAttribute("data-floor"))
            .ShouldBe(["10", "9", "8", "7", "6", "5", "4", "3", "2", "1"]);
    }

    [Fact]
    public void Landings_FollowTheConfiguredBuilding()
    {
        var shaft = RenderShaft(TestData.Status(floor: 0), TestData.Configuration(lowestFloor: 0, highestFloor: 3));

        shaft.FindAll(".landing").Count.ShouldBe(4);
        shaft.Find(".building").GetAttribute("style").ShouldNotBeNull().ShouldContain("--es-floor-count: 4");
    }

    [Fact]
    public void HallButtons_TopFloor_HasOnlyDown()
    {
        var shaft = RenderShaft(TestData.Status());

        DirectionsOn(shaft, 10).ShouldBe(["Down"], "nobody can go up from the top floor");
    }

    [Fact]
    public void HallButtons_BottomFloor_HasOnlyUp()
    {
        var shaft = RenderShaft(TestData.Status());

        DirectionsOn(shaft, 1).ShouldBe(["Up"], "nobody can go down from the bottom floor");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(9)]
    public void HallButtons_MiddleFloor_HasUpAndDown(int floor)
    {
        var shaft = RenderShaft(TestData.Status());

        DirectionsOn(shaft, floor).ShouldBe(["Up", "Down"]);
    }

    [Fact]
    public void HallButtons_HaveDescriptiveAccessibleNames()
    {
        var shaft = RenderShaft(TestData.Status());

        HallButton(shaft, 3, CallDirection.Up).GetAttribute("aria-label").ShouldBe("Call elevator going up from floor 3");
        HallButton(shaft, 3, CallDirection.Down).GetAttribute("aria-label").ShouldBe("Call elevator going down from floor 3");
        shaft.Find(".landing[data-floor='3'] [role=group]").GetAttribute("aria-label").ShouldBe("Hall buttons, floor 3");
    }

    [Fact]
    public void HallButtons_AreRealButtonsThatDoNotSubmitForms()
    {
        var shaft = RenderShaft(TestData.Status());

        shaft.FindAll(".hall-button").ShouldAllBe(button => button.TagName == "BUTTON" && button.GetAttribute("type") == "button");
    }

    [Fact]
    public void HallButton_PendingHallCall_IsLit()
    {
        var shaft = RenderShaft(TestData.Status(pending: TestData.HallCall(7, CallDirection.Down)));

        HallButton(shaft, 7, CallDirection.Down).GetAttribute("aria-pressed").ShouldBe("true");
    }

    [Fact]
    public void HallButtons_OnlyTheMatchingFloorAndDirectionAreLit()
    {
        var shaft = RenderShaft(TestData.Status(pending:
        [
            TestData.HallCall(7, CallDirection.Down),
            TestData.CarCall(3),
            TestData.HallCall(4, CallDirection.Up),
        ]));

        var lit = shaft.FindAll(".hall-button[aria-pressed=true]")
            .Select(button => $"{button.GetAttribute("data-floor")} {button.GetAttribute("data-direction")}");

        lit.ShouldBe(["7 Down", "4 Up"], ignoreOrder: true, "a car call to floor 3 must not light a hall button there");
        shaft.FindAll(".hall-button[aria-pressed=false]").Count.ShouldBe(18 - 2);
    }

    [Fact]
    public void HallButton_Click_ReportsFloorAndDirection()
    {
        HallCallRequest? pressed = null;
        var shaft = Render<ElevatorShaft>(parameters => parameters
            .Add(shaft => shaft.Configuration, TestData.Configuration())
            .Add(shaft => shaft.Status, TestData.Status())
            .Add(shaft => shaft.OnHallCall, call => pressed = call));

        HallButton(shaft, 6, CallDirection.Down).Click();

        pressed.ShouldBe(new HallCallRequest(6, CallDirection.Down));
    }

    [Theory]
    [InlineData(1, "translateY(calc(0 * var(--es-floor-height)))")]
    [InlineData(4, "translateY(calc(-3 * var(--es-floor-height)))")]
    [InlineData(10, "translateY(calc(-9 * var(--es-floor-height)))")]
    public void Car_IsPositionedByTransformRelativeToTheLowestFloor(int floor, string transform)
    {
        var shaft = RenderShaft(TestData.Status(floor: floor));

        shaft.Find(".car").GetAttribute("style").ShouldNotBeNull().ShouldContain($"transform: {transform}");
    }

    [Fact]
    public void Car_InABuildingStartingAtZero_IsOffsetFromFloorZero()
    {
        var shaft = RenderShaft(TestData.Status(floor: 2), TestData.Configuration(lowestFloor: 0, highestFloor: 3));

        shaft.Find(".car").GetAttribute("style").ShouldNotBeNull().ShouldContain("translateY(calc(-2 * var(--es-floor-height)))");
    }

    [Theory]
    [InlineData(1.5, "1500ms")]
    [InlineData(2.25, "2250ms")]
    public void Car_AnimatesOverTheConfiguredTravelTimePerFloor(double secondsPerFloor, string duration)
    {
        var shaft = RenderShaft(TestData.Status(floor: 3), TestData.Configuration(travelSecondsPerFloor: secondsPerFloor));

        shaft.Find(".car").GetAttribute("style").ShouldNotBeNull().ShouldContain($"transition-duration: {duration}");
    }

    [Fact]
    public void Car_DoorOpen_OpensTheDoorsAndSaysSo()
    {
        var shaft = RenderShaft(TestData.Status(floor: 5, status: ElevatorStatus.DoorOpen));

        shaft.Find(".car").ClassList.ShouldContain("car--doors-open");
        shaft.Find(".shaft").GetAttribute("aria-label").ShouldBe("Car at floor 5, doors open");
    }

    [Theory]
    [InlineData(ElevatorStatus.Idle)]
    [InlineData(ElevatorStatus.MovingUp)]
    [InlineData(ElevatorStatus.MovingDown)]
    [InlineData(ElevatorStatus.OutOfService)]
    public void Car_AnyOtherState_KeepsTheDoorsClosed(ElevatorStatus status)
    {
        var shaft = RenderShaft(TestData.Status(floor: 5, status: status));

        shaft.Find(".car").ClassList.ShouldContain("car--doors-closed");
        shaft.Find(".car").ClassList.ShouldNotContain("car--doors-open");
        shaft.Find(".shaft").GetAttribute("aria-label").ShouldBe("Car at floor 5, doors closed");
    }

    [Fact]
    public void Car_OutOfService_IsMarked()
    {
        var shaft = RenderShaft(TestData.Status(status: ElevatorStatus.OutOfService, cause: OutOfServiceCause.Fault));

        shaft.Find(".car").ClassList.ShouldContain("car--out-of-service");
    }

    [Fact]
    public void Landing_WhereTheCarIs_IsTheCurrentLocation()
    {
        var shaft = RenderShaft(TestData.Status(floor: 6));

        shaft.FindAll("[aria-current=location]").ShouldHaveSingleItem().GetAttribute("data-floor").ShouldBe("6");
    }

    [Fact]
    public void Car_BeforeTheFirstStatus_WaitsAtTheLowestFloorWithDoorsClosed()
    {
        var shaft = RenderShaft(status: null);

        shaft.Find(".car").GetAttribute("data-floor").ShouldBe("1");
        shaft.Find(".car").ClassList.ShouldContain("car--doors-closed");
    }

    private IRenderedComponent<ElevatorShaft> RenderShaft(ElevatorStatusDto? status, ElevatorConfigurationDto? configuration = null) =>
        Render<ElevatorShaft>(parameters => parameters
            .Add(shaft => shaft.Configuration, configuration ?? TestData.Configuration())
            .Add(shaft => shaft.Status, status));

    private static IElement HallButton(IRenderedComponent<ElevatorShaft> shaft, int floor, CallDirection direction) =>
        shaft.Find($".hall-button[data-floor='{floor}'][data-direction='{direction}']");

    private static string?[] DirectionsOn(IRenderedComponent<ElevatorShaft> shaft, int floor) =>
        [.. shaft.FindAll($".landing[data-floor='{floor}'] .hall-button").Select(button => button.GetAttribute("data-direction"))];
}
