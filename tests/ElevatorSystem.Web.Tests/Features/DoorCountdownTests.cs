using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Features.Building;

namespace ElevatorSystem.Web.Tests.Features;

/// <summary>The door countdown: 5 s doors shown as a 4 s countdown (one second of room for lag), on the server's clock.</summary>
public sealed class DoorCountdownTests : WebTestContext
{
    private static readonly TimeSpan DoorTime = TimeSpan.FromSeconds(5);

    [Fact]
    public void DoorsClosing_ShowTheSecondsLeftAndABarThatEmpties()
    {
        var countdown = RenderCountdown(Open(closesInSeconds: 4));

        countdown.Find(".door-countdown__label").TextContent.ShouldBe("Doors close in 3 s");
        countdown.Find(".door-countdown__bar").GetAttribute("style").ShouldBe("width: 75%");
        countdown.Find("[data-testid=door-countdown]").GetAttribute("role").ShouldBe("timer");
    }

    [Fact]
    public void Countdown_RunsWithTheClock()
    {
        var countdown = RenderCountdown(Open(closesInSeconds: 5));

        Clock.Advance(TimeSpan.FromSeconds(2.6));

        countdown.WaitForAssertion(() => countdown.Find(".door-countdown__label").TextContent.ShouldBe("Doors close in 2 s"));
        countdown.Find(".door-countdown__bar").GetAttribute("style").ShouldBe("width: 35%");
    }

    [Fact]
    public void Countdown_EndsASecondBeforeTheDoorsClose()
    {
        var countdown = RenderCountdown(Open(closesInSeconds: 1));

        countdown.Find(".door-countdown__label").TextContent.ShouldBe("Doors close in 0 s");
        countdown.Find(".door-countdown__bar").GetAttribute("style").ShouldBe("width: 0%", "the last second is room for a late press to reach the server");
    }

    [Fact]
    public void Countdown_NeverRunsBelowZeroOrAboveTheFullBar()
    {
        RenderCountdown(Open(closesInSeconds: -1)).Find(".door-countdown__bar").GetAttribute("style").ShouldBe("width: 0%");
        RenderCountdown(Open(closesInSeconds: 9)).Find(".door-countdown__bar").GetAttribute("style").ShouldBe("width: 100%");
    }

    [Fact]
    public void Countdown_FollowsTheServersClock_EvenWhenTheBrowsersIsOff()
    {
        // The server's clock is 12 s ahead of the browser's (as a Docker Desktop VM can be). A countdown measured on the
        // browser's clock would sit full at "4 s" until the doors closed.
        var serverNow = TestData.Noon.AddSeconds(12);
        var status = TestData.Status(status: ElevatorStatus.DoorOpen) with { TakenAt = serverNow, DoorsCloseAt = serverNow.AddSeconds(4) };
        Store.ApplyStatus(status);

        var countdown = RenderCountdown(status);

        countdown.Find(".door-countdown__label").TextContent.ShouldBe("Doors close in 3 s");
        countdown.Find(".door-countdown__bar").GetAttribute("style").ShouldBe("width: 75%");
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
            .Add(countdown => countdown.Duration, DoorTime)
            .Add(countdown => countdown.ShowClosed, showClosed));

    private static ElevatorStatusDto Open(double closesInSeconds) =>
        TestData.Status(status: ElevatorStatus.DoorOpen) with { DoorsCloseAt = TestData.Noon.AddSeconds(closesInSeconds) };
}
