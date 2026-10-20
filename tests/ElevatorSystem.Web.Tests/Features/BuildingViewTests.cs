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
        lobby.GetAttribute("aria-label").ShouldBe("2 waiting, 1 on the floor");
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
        building.Find(".storey[data-floor='1'] .storey__people").GetAttribute("aria-label").ShouldBeNullOrEmpty();
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
