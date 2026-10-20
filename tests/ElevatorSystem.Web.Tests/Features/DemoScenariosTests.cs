using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Features.Admin;
using ElevatorSystem.Web.Services;

namespace ElevatorSystem.Web.Tests.Features;

public sealed class DemoScenariosTests : WebTestContext
{
    [Fact]
    public void EveryScenario_HasACardWithATitleAndWhatItDoes()
    {
        var demos = Render<DemoScenarios>();

        demos.FindAll(".demo-card").Select(card => card.GetAttribute("data-testid"))
            .ShouldBe(["demo-MorningRush", "demo-LunchBreak", "demo-BusyDay"]);
        demos.FindAll(".demo-card").ShouldAllBe(card => card.QuerySelector(".demo-card__title")!.TextContent.Length > 0
            && card.QuerySelector(".demo-card__hint")!.TextContent.Length > 0);
    }

    [Fact]
    public void Card_Pressed_StartsTheScenario_AndSaysSo()
    {
        var demos = Render<DemoScenarios>();

        demos.Find("[data-testid=demo-MorningRush]").Click();

        Staff.Scenarios.ShouldBe([DemoScenario.MorningRush]);
        var message = demos.Find("[data-testid=demo-message]");
        message.TextContent.ShouldBe("Morning rush started. Watch the building.");
        message.GetAttribute("role").ShouldBe("status");
        message.ClassList.ShouldNotContain("demo-message--error");
    }

    [Fact]
    public void Stop_SendsThePassengersHome()
    {
        var demos = Render<DemoScenarios>();

        demos.Find("[data-testid=demo-stop]").Click();

        Staff.Scenarios.ShouldBe([null]);
        demos.Find("[data-testid=demo-message]").TextContent.ShouldBe("The demo passengers are leaving.");
    }

    [Fact]
    public void ScenariosDisabledOnTheServer_IsExplained()
    {
        Staff.ScenarioAnswer = StaffResult.Fail<ScenarioAcceptedResponse>(StaffFailure.Conflict);
        var demos = Render<DemoScenarios>();

        demos.Find("[data-testid=demo-BusyDay]").Click();

        var message = demos.Find("[data-testid=demo-message]");
        message.TextContent.ShouldBe("Demo scenarios are disabled here.");
        message.ClassList.ShouldContain("demo-message--error");
    }
}
