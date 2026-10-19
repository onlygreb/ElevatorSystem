using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Features.Passenger;

namespace ElevatorSystem.Web.Tests.Features;

public sealed class RequestQueueTests : WebTestContext
{
    [Fact]
    public void Chips_KeepTheServersFifoOrder()
    {
        var queue = RenderQueue(TestData.Status(pending:
        [
            TestData.HallCall(7, CallDirection.Down),
            TestData.CarCall(2),
            TestData.HallCall(4, CallDirection.Up),
        ]));

        queue.FindAll(".queue-chip .queue-chip__text").Select(chip => chip.TextContent)
            .ShouldBe(["Floor 7, going down", "Floor 2, from the car", "Floor 4, going up"]);
        queue.FindAll(".queue-chip__position").Select(position => position.TextContent).ShouldBe(["1", "2", "3"]);
    }

    [Fact]
    public void Chips_AreAnOrderedList()
    {
        var queue = RenderQueue(TestData.Status(pending: TestData.CarCall(2)));

        queue.Find("ol.queue > li.queue-chip").ShouldNotBeNull();
    }

    [Fact]
    public void Chip_MergedRequests_ShowsTheCount()
    {
        var queue = RenderQueue(TestData.Status(pending: TestData.HallCall(3, CallDirection.Up, requestCount: 4)));

        queue.Find(".queue-chip__count").TextContent.ShouldBe("4 requests");
    }

    [Fact]
    public void Chip_SingleRequest_ShowsNoCount()
    {
        var queue = RenderQueue(TestData.Status(pending: TestData.HallCall(3, CallDirection.Up)));

        queue.FindAll(".queue-chip__count").ShouldBeEmpty();
    }

    [Fact]
    public void EmptyQueue_SaysSo()
    {
        var queue = RenderQueue(TestData.Status());

        queue.Find(".es-empty").TextContent.ShouldBe("No pending calls.");
        queue.FindAll(".queue").ShouldBeEmpty();
    }

    private IRenderedComponent<RequestQueue> RenderQueue(ElevatorStatusDto status) =>
        Render<RequestQueue>(parameters => parameters.Add(queue => queue.Status, status));
}
