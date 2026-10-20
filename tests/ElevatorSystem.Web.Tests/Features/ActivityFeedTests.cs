using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Features.Passenger;

namespace ElevatorSystem.Web.Tests.Features;

public sealed class ActivityFeedTests : WebTestContext
{
    private static readonly DateTimeOffset At = TestData.Noon;
    private static readonly CallDto HallUp3 = new(CallKind.Hall, 3, CallDirection.Up);
    private static readonly CallDto Car9 = new(CallKind.Car, 9, null);

    private static readonly Dictionary<string, (ElevatorActivityDto Activity, string Expected)> Cases = new()
    {
        ["CallQueued"] = (new(At, ElevatorActivityKind.CallQueued, 3, Call: HallUp3, RequestId: Guid.NewGuid(), Position: 2), "Call for floor 3 going up queued at position 2."),
        ["CallMerged"] = (new(At, ElevatorActivityKind.CallMerged, 9, Call: Car9, RequestId: Guid.NewGuid(), Position: 1), "Call for floor 9 from the car joined a waiting call at position 1."),
        ["RequestRejected"] = (new(At, ElevatorActivityKind.RequestRejected, 3, Call: HallUp3, RejectionReason: RejectionReason.OutOfService), "Call for floor 3 going up refused: The elevator is out of service right now. Please use the stairs or try again later."),
        ["Moved"] = (new(At, ElevatorActivityKind.Moved, 4, FromFloor: 3, Direction: CallDirection.Up), "Moved from floor 3 to floor 4."),
        ["DoorsOpened"] = (new(At, ElevatorActivityKind.DoorsOpened, 4), "Doors opened at floor 4."),
        ["CallServed"] = (new(At, ElevatorActivityKind.CallServed, 3, Call: HallUp3, RequestCount: 1, WaitTime: TimeSpan.FromSeconds(12.34)), "Served call for floor 3 going up after 12.3 s."),
        ["CallServedMerged"] = (new(At, ElevatorActivityKind.CallServed, 9, Call: Car9, RequestCount: 3, WaitTime: TimeSpan.FromSeconds(4)), "Served call for floor 9 from the car after 4.0 s; 3 requests were merged into it."),
        ["DoorsClosed"] = (new(At, ElevatorActivityKind.DoorsClosed, 4), "Doors closed at floor 4."),
        ["OutOfService"] = (new(At, ElevatorActivityKind.OutOfService, 6, OutOfServiceCause: OutOfServiceCause.EmergencyStop, CancelledCalls: 2), "Out of service at floor 6: emergency stop. Calls cancelled: 2."),
        ["ReturnedToService"] = (new(At, ElevatorActivityKind.ReturnedToService, 6), "Back in service at floor 6."),
    };

    public static TheoryData<string> EveryKind => [.. Cases.Keys];

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void Entry_DescribesTheActivityInWords(string activityCase)
    {
        var (activity, expected) = Cases[activityCase];

        var feed = RenderFeed(activity);

        feed.Find(".activity__text").TextContent.ShouldBe(expected);
    }

    [Fact]
    public void Entries_KeepTheOrderGiven_NewestFirst()
    {
        var feed = RenderFeed(TestData.DoorsOpened(4, secondsAfterNoon: 2), TestData.Moved(3, 4, secondsAfterNoon: 1));

        feed.FindAll(".activity__item").Select(item => item.GetAttribute("data-kind")).ShouldBe(["DoorsOpened", "Moved"]);
    }

    [Fact]
    public void Entry_CarriesAMachineReadableTime()
    {
        var feed = RenderFeed(TestData.DoorsOpened(4, secondsAfterNoon: 5));

        feed.Find("time").GetAttribute("datetime").ShouldBe("2026-10-19T12:00:05.0000000+00:00");
    }

    [Fact]
    public void Entry_Refused_IsMarkedBeyondColour()
    {
        var feed = RenderFeed(new ElevatorActivityDto(At, ElevatorActivityKind.RequestRejected, 12, RejectionReason: RejectionReason.InvalidFloor));

        feed.Find(".activity__item").ClassList.ShouldContain("activity__item--alert");
        feed.Find(".activity__icon").TextContent.ShouldBe("!");
    }

    [Fact]
    public void ScrollableFeed_CanBeReachedByKeyboard()
    {
        var feed = RenderFeed(TestData.DoorsOpened(4));

        var region = feed.Find(".activity-scroll");
        region.GetAttribute("tabindex").ShouldBe("0");
        region.GetAttribute("role").ShouldBe("region");
    }

    [Fact]
    public void NoActivity_SaysSo()
    {
        var feed = RenderFeed();

        feed.Find(".es-empty").TextContent.ShouldBe("Nothing has happened yet.");
    }

    private IRenderedComponent<ActivityFeed> RenderFeed(params ElevatorActivityDto[] activity) =>
        Render<ActivityFeed>(parameters => parameters.Add(feed => feed.Activity, activity));
}
