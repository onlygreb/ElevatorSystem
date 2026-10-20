using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Features.Admin;

namespace ElevatorSystem.Web.Tests.Features;

/// <summary>The admin's people panel: the average wait says which scheduling it was measured under (ADR-0005).</summary>
public sealed class OccupancyPanelTests : WebTestContext
{
    private static readonly BuildingOccupancyDto Counts =
        new(2, 8, [.. Enumerable.Range(1, 10).Select(floor => new FloorOccupancyDto(floor, 0, 0))], 5, TimeSpan.FromSeconds(12), TestData.Noon);

    [Fact]
    public void AverageWait_SaysWhichSchedulingItWasMeasuredUnder()
    {
        var panel = Render<OccupancyPanel>(parameters => parameters
            .Add(panel => panel.Occupancy, Counts)
            .Add(panel => panel.Scheduling, "LOOK"));

        panel.Find("[data-testid=scheduling]").TextContent.ShouldBe("with LOOK scheduling");
    }

    [Fact]
    public void WithoutTheConfiguration_SaysNothingAboutScheduling()
    {
        var panel = Render<OccupancyPanel>(parameters => parameters.Add(panel => panel.Occupancy, Counts));

        panel.FindAll("[data-testid=scheduling]").ShouldBeEmpty();
    }
}
