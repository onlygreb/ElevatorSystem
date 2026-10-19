using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Features.Passenger;

namespace ElevatorSystem.Web.Tests.Features;

public sealed class CarPanelTests : WebTestContext
{
    [Fact]
    public void Buttons_OnePerFloorFromTheTopDown()
    {
        var panel = RenderPanel(TestData.Status());

        panel.FindAll(".car-button").Select(button => button.TextContent.Trim())
            .ShouldBe(["10", "9", "8", "7", "6", "5", "4", "3", "2", "1"]);
    }

    [Fact]
    public void Buttons_HaveAccessibleNamesContainingTheirVisibleNumber()
    {
        var panel = RenderPanel(TestData.Status());

        panel.FindAll(".car-button").Select(button => button.GetAttribute("aria-label"))
            .ShouldBe([.. Enumerable.Range(1, 10).Reverse().Select(floor => $"Go to floor {floor}")]);
    }

    [Fact]
    public void Button_PendingCarCall_IsLit()
    {
        var panel = RenderPanel(TestData.Status(pending: [TestData.CarCall(8), TestData.HallCall(2, CallDirection.Up)]));

        panel.FindAll(".car-button[aria-pressed=true]").Select(button => button.GetAttribute("data-floor"))
            .ShouldBe(["8"], "a hall call to floor 2 is not a car call");
        panel.FindAll(".car-button[aria-pressed=false]").Count.ShouldBe(9);
    }

    [Fact]
    public void Button_Click_ReportsTheFloor()
    {
        int? chosen = null;
        var panel = Render<CarPanel>(parameters => parameters
            .Add(panel => panel.Configuration, TestData.Configuration())
            .Add(panel => panel.OnSelectFloor, floor => chosen = floor));

        panel.Find(".car-button[data-floor='4']").Click();

        chosen.ShouldBe(4);
    }

    private IRenderedComponent<CarPanel> RenderPanel(ElevatorStatusDto status) =>
        Render<CarPanel>(parameters => parameters
            .Add(panel => panel.Configuration, TestData.Configuration())
            .Add(panel => panel.Status, status));
}
