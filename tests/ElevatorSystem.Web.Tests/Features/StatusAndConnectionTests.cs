using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Features.Passenger;
using ElevatorSystem.Web.Services;

namespace ElevatorSystem.Web.Tests.Features;

public sealed class StatusAndConnectionTests : WebTestContext
{
    [Theory]
    [InlineData(ElevatorStatus.Idle, "Idle")]
    [InlineData(ElevatorStatus.MovingUp, "Going up")]
    [InlineData(ElevatorStatus.MovingDown, "Going down")]
    [InlineData(ElevatorStatus.DoorOpen, "Doors open")]
    [InlineData(ElevatorStatus.OutOfService, "Out of service")]
    public void StatusLine_NamesTheStateInWords(ElevatorStatus status, string expected)
    {
        var line = Render<StatusLine>(parameters => parameters.Add(line => line.Status, TestData.Status(floor: 4, status: status)));

        line.Find("[data-testid=status-floor]").TextContent.ShouldContain("4");
        line.Find("[data-testid=status-state]").TextContent.Trim().ShouldBe(expected);
    }

    [Theory]
    [InlineData(OutOfServiceCause.Maintenance, "Cause: maintenance")]
    [InlineData(OutOfServiceCause.Fault, "Cause: fault")]
    [InlineData(OutOfServiceCause.EmergencyStop, "Cause: emergency stop")]
    public void StatusLine_OutOfService_GivesTheCause(OutOfServiceCause cause, string expected)
    {
        var line = Render<StatusLine>(parameters => parameters
            .Add(line => line.Status, TestData.Status(status: ElevatorStatus.OutOfService, cause: cause)));

        line.Find("[data-testid=status-out-of-service]").TextContent.Trim().ShouldBe(expected);
    }

    [Fact]
    public void StatusLine_InService_ShowsNoCause()
    {
        var line = Render<StatusLine>(parameters => parameters.Add(line => line.Status, TestData.Status()));

        line.FindAll("[data-testid=status-out-of-service]").ShouldBeEmpty();
    }

    [Fact]
    public void StatusLine_BeforeTheFirstStatus_SaysItIsWaiting()
    {
        var line = Render<StatusLine>();

        line.Markup.ShouldContain("Waiting for the elevator's status…");
    }

    [Fact]
    public void Banner_Reconnecting_TellsThePassenger()
    {
        var banner = Render<ConnectionBanner>(parameters => parameters.Add(banner => banner.State, ConnectionState.Reconnecting));

        var alert = banner.Find("[data-testid=connection-banner]");
        alert.TextContent.ShouldContain("Reconnecting…");
        alert.GetAttribute("role").ShouldBe("status");
    }

    [Fact]
    public void Banner_Connecting_SaysItIsConnecting()
    {
        var banner = Render<ConnectionBanner>(parameters => parameters.Add(banner => banner.State, ConnectionState.Connecting));

        banner.Find("[data-testid=connection-banner]").TextContent.ShouldContain("Connecting to live updates…");
    }

    [Fact]
    public void Banner_Connected_IsHidden()
    {
        var banner = Render<ConnectionBanner>(parameters => parameters.Add(banner => banner.State, ConnectionState.Connected));

        banner.FindAll("[data-testid=connection-banner]").ShouldBeEmpty();
    }

    [Fact]
    public void Announcer_DoorsOpening_IsAnnouncedPolitely()
    {
        var announcer = Render<ArrivalAnnouncer>();

        Store.AddActivity(TestData.DoorsOpened(5));

        var region = announcer.Find("[data-testid=announcer]");
        region.GetAttribute("aria-live").ShouldBe("polite");
        announcer.WaitForAssertion(() => announcer.Find("[data-testid=announcer]").TextContent.ShouldBe("Floor 5, doors opening"));
    }

    [Fact]
    public void Announcer_OutOfService_IsAnnounced()
    {
        var announcer = Render<ArrivalAnnouncer>();

        Store.AddActivity(new ElevatorActivityDto(TestData.Noon, ElevatorActivityKind.OutOfService, 3, OutOfServiceCause: OutOfServiceCause.Maintenance));

        announcer.WaitForAssertion(() => announcer.Find("[data-testid=announcer]").TextContent.ShouldBe("Elevator out of service: maintenance"));
    }

    [Fact]
    public void Announcer_PassingAFloor_StaysQuiet()
    {
        var announcer = Render<ArrivalAnnouncer>();
        Store.AddActivity(TestData.DoorsOpened(5));

        Store.AddActivity(TestData.Moved(5, 6));

        announcer.Find("[data-testid=announcer]").TextContent.ShouldBe("Floor 5, doors opening", "moving between floors is not announced");
    }
}
