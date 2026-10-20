using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Features.Building;

namespace ElevatorSystem.Web.Tests.Features;

public sealed class BuildingViewTests : WebTestContext
{
    [Fact]
    public void Floors_AreListedTopFirst_WithTheLobbyNamed()
    {
        var building = RenderBuilding();

        building.FindAll(".storey").Select(storey => storey.GetAttribute("data-floor")).ShouldBe(["10", "9", "8", "7", "6", "5", "4", "3", "2", "1"]);
        building.Find(".storey[data-floor='1'] .storey__name").TextContent.ShouldBe("Lobby");
        building.Find(".storey[data-floor='5'] .storey__name").TextContent.ShouldBeEmpty();
    }

    [Fact]
    public void Car_SitsAtItsFloor_AndGlidesOverTheTravelTime()
    {
        var building = RenderBuilding(TestData.Status(floor: 4, status: ElevatorStatus.MovingUp));

        var car = building.Find("[data-testid=car]");
        car.GetAttribute("data-floor").ShouldBe("4");
        car.GetAttribute("style")!.ShouldContain("translateY(calc(-3 * var(--es-floor-height)))");
        car.GetAttribute("style")!.ShouldContain("transition-duration: 1500ms");
        car.ClassList.ShouldContain("cabin--moving");
        building.Find(".storey[data-floor='4']").ClassList.ShouldContain("storey--car");
    }

    [Fact]
    public void Car_FastForwarded_GlidesThatMuchFaster()
    {
        var building = RenderBuilding(TestData.Status(floor: 4, status: ElevatorStatus.MovingUp) with { Speed = 6 });

        building.Find("[data-testid=car]").GetAttribute("style")!.ShouldContain("transition-duration: 250ms");
    }

    [Fact]
    public void Car_GlidesTheWholeDistance_WhenAStatusSkipsFloors_WithItsDoorsShutUntilItArrives()
    {
        var building = RenderBuilding(TestData.Status(floor: 1, status: ElevatorStatus.DoorOpen));

        building.Render(parameters => parameters.Add(view => view.Status, TestData.Status(floor: 4, status: ElevatorStatus.DoorOpen, version: 2)));

        var car = building.Find("[data-testid=car]");
        car.GetAttribute("style")!.ShouldContain("transition-duration: 4500ms", customMessage: "three floors at 1.5 s each, not one");
        car.ClassList.ShouldContain("cabin--closed", "the doors open once the car gets there");
        Clock.Advance(TimeSpan.FromSeconds(4.5));
        building.WaitForAssertion(() => building.Find("[data-testid=car]").ClassList.ShouldContain("cabin--open"));
    }

    [Fact]
    public void Car_ANewFloorMidGlide_GlidesOnFromWhereItIsOnScreen_AtTheSamePace()
    {
        var building = RenderBuilding(TestData.Status(floor: 1));
        building.Render(parameters => parameters.Add(view => view.Status, TestData.Status(floor: 2, status: ElevatorStatus.MovingUp, version: 2)));

        Clock.Advance(TimeSpan.FromSeconds(0.75));
        building.Render(parameters => parameters.Add(view => view.Status, TestData.Status(floor: 3, status: ElevatorStatus.MovingUp, version: 3)));

        building.Find("[data-testid=car]").GetAttribute("style")!.ShouldContain("transition-duration: 2250ms", customMessage: "halfway past floor 1, one and a half floors to go");
    }

    [Fact]
    public void Car_ALongJump_GlidesAtMostThreeFloorsLong()
    {
        var building = RenderBuilding(TestData.Status(floor: 1));

        building.Render(parameters => parameters.Add(view => view.Status, TestData.Status(floor: 10, version: 2)));

        building.Find("[data-testid=car]").GetAttribute("style")!.ShouldContain("transition-duration: 4500ms");
    }

    [Fact]
    public void Doors_FastForwarded_SlideThatMuchFaster()
    {
        var building = RenderBuilding(TestData.Status(floor: 2, status: ElevatorStatus.DoorOpen) with { Speed = 8 });

        building.Find("[data-testid=car]").GetAttribute("style")!.ShouldContain("--es-door-duration: calc(var(--es-door-slide) / 8)");
    }

    [Theory]
    [InlineData(ElevatorStatus.DoorOpen, "cabin--open", "open")]
    [InlineData(ElevatorStatus.Idle, "cabin--closed", "closed")]
    public void Car_ShowsItsDoors_AndDescribesThemInWords(ElevatorStatus status, string doorClass, string doors)
    {
        var building = RenderBuilding(TestData.Status(floor: 6, status: status));

        building.Find("[data-testid=car]").ClassList.ShouldContain(doorClass);
        var description = building.Find(".tower__shaft").GetAttribute("aria-label")!;
        description.ShouldContain("6");
        description.ShouldContain(doors, Case.Insensitive);
    }

    [Fact]
    public void Car_OutOfService_IsMarked()
    {
        var building = RenderBuilding(TestData.Status(status: ElevatorStatus.OutOfService, cause: OutOfServiceCause.Maintenance));

        building.Find("[data-testid=car]").ClassList.ShouldContain("cabin--out-of-service");
    }

    [Fact]
    public void People_AreDrawnPerFloor_AndCountedForScreenReaders()
    {
        var building = RenderBuilding(occupancy: Occupancy(3, (1, 2, 1), (7, 0, 4)));

        var lobby = building.Find(".storey[data-floor='1'] .storey__people");
        lobby.QuerySelectorAll(".person--waiting").Length.ShouldBe(2);
        lobby.QuerySelectorAll(".person--present").Length.ShouldBe(1);
        lobby.QuerySelector("[data-testid=floor-people]")!.TextContent.ShouldBe("2 waiting, 1 on the floor");
        building.Find(".storey[data-floor='7'] .storey__people").QuerySelectorAll(".person--present").Length.ShouldBe(4);
        building.FindAll(".cabin__riders .person--riding").Count.ShouldBe(3);
    }

    [Fact]
    public void People_BeyondSixDots_ShowAsACount()
    {
        var building = RenderBuilding(occupancy: Occupancy(8, (1, 15, 0)));

        var waiting = building.FindAll(".storey[data-floor='1'] .person--waiting");
        waiting.Count.ShouldBe(BuildingView.MaxDots);
        waiting[^1].TextContent.ShouldBe("+10");
        building.FindAll(".cabin__riders .person--riding")[^1].TextContent.ShouldBe("+3");
    }

    [Fact]
    public void WithoutOccupancy_NoCountsAreShown()
    {
        var building = RenderBuilding();

        building.FindAll(".storey .person--waiting, .storey .person--present").ShouldBeEmpty();
        building.FindAll("[data-testid=floor-people]").ShouldBeEmpty();
    }

    [Fact]
    public void You_OnAFloor_AreMarkedThere()
    {
        var building = Render<BuildingView>(parameters => parameters
            .Add(view => view.Configuration, TestData.Configuration())
            .Add(view => view.YouFloor, 5));

        building.Find(".storey[data-floor='5'] [data-testid=you-on-floor]").TextContent.ShouldBe("You");
        building.Find(".storey[data-floor='5']").ClassList.ShouldContain("storey--you");
        building.FindAll("[data-testid=you-in-car]").ShouldBeEmpty();
    }

    [Fact]
    public void You_InTheCar_RideInsideIt_AndAreNotCountedTwice()
    {
        var building = Render<BuildingView>(parameters => parameters
            .Add(view => view.Configuration, TestData.Configuration())
            .Add(view => view.Occupancy, Occupancy(2))
            .Add(view => view.YouFloor, 3)
            .Add(view => view.YouInCar, true));

        building.Find(".cabin__riders [data-testid=you-in-car]").ShouldNotBeNull();
        building.FindAll(".cabin__riders .person--riding").Count.ShouldBe(1);
        building.FindAll("[data-testid=you-on-floor]").ShouldBeEmpty();
    }

    private IRenderedComponent<BuildingView> RenderBuilding(ElevatorStatusDto? status = null, BuildingOccupancyDto? occupancy = null) =>
        Render<BuildingView>(parameters => parameters
            .Add(view => view.Configuration, TestData.Configuration())
            .Add(view => view.Status, status)
            .Add(view => view.Occupancy, occupancy));

    private static BuildingOccupancyDto Occupancy(int inCar, params (int Floor, int Waiting, int Present)[] floors) =>
        new(inCar, 8, [.. floors.Select(floor => new FloorOccupancyDto(floor.Floor, floor.Waiting, floor.Present))], 0, null, TestData.Noon);
}
