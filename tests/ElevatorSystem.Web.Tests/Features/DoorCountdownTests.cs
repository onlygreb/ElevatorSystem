using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Features.Building;

namespace ElevatorSystem.Web.Tests.Features;

public sealed class DoorCountdownTests : WebTestContext
{
    [Fact]
    public void DoorsClosing_ShowTheSecondsLeftAndABarThatEmpties()
    {
        var countdown = RenderCountdown(Open(closesInSeconds: 3));

        countdown.Find(".door-countdown__label").TextContent.ShouldBe("Doors close in 3 s");
        countdown.Find(".door-countdown__bar").GetAttribute("style").ShouldBe("width: 75%");
        countdown.Find("[data-testid=door-countdown]").GetAttribute("role").ShouldBe("timer");
    }

    [Fact]
    public void Countdown_RunsWithTheClock()
    {
        var countdown = RenderCountdown(Open(closesInSeconds: 4));

        Clock.Advance(TimeSpan.FromSeconds(2.6));

        countdown.WaitForAssertion(() => countdown.Find(".door-countdown__label").TextContent.ShouldBe("Doors close in 2 s"));
        countdown.Find(".door-countdown__bar").GetAttribute("style").ShouldBe("width: 35%");
    }

    [Fact]
    public void Countdown_NeverRunsBelowZeroOrAboveTheFullBar()
    {
        RenderCountdown(Open(closesInSeconds: -1)).Find(".door-countdown__bar").GetAttribute("style").ShouldBe("width: 0%");
        RenderCountdown(Open(closesInSeconds: 9)).Find(".door-countdown__bar").GetAttribute("style").ShouldBe("width: 100%");
    }

    [Fact]
    public void DoorsHeldOpen_SayTheCarIsWaiting_WithNoCountdown()
    {
        var countdown = RenderCountdown(TestData.Status(status: ElevatorStatus.DoorOpen));

        countdown.Find("[data-testid=door-held]").TextContent.ShouldContain("Doors open, waiting for a call");
        countdown.FindAll("[data-testid=door-countdown]").ShouldBeEmpty();
    }

    [Fact]
    public void DoorsClosed_ShowNothing_UnlessAsked()
    {
        RenderCountdown(TestData.Status(status: ElevatorStatus.MovingUp)).Markup.Trim().ShouldBeEmpty();

        RenderCountdown(TestData.Status(status: ElevatorStatus.MovingUp), showClosed: true)
            .Find("[data-testid=door-closed]").TextContent.ShouldContain("Doors closed");
    }

    private IRenderedComponent<DoorCountdown> RenderCountdown(ElevatorStatusDto status, bool showClosed = false) =>
        Render<DoorCountdown>(parameters => parameters
            .Add(countdown => countdown.Status, status)
            .Add(countdown => countdown.Duration, TimeSpan.FromSeconds(4))
            .Add(countdown => countdown.ShowClosed, showClosed));

    private static ElevatorStatusDto Open(double closesInSeconds) =>
        TestData.Status(status: ElevatorStatus.DoorOpen) with { DoorsCloseAt = TestData.Noon.AddSeconds(closesInSeconds) };
}
