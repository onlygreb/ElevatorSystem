using System.Net;
using ElevatorSystem.Application;
using ElevatorSystem.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ElevatorSystem.Api.Tests;

public sealed class HostingTests
{
    [Fact]
    public async Task ControlLoop_RunsInTheHost_AndServesRequests()
    {
        await using var factory = new ApiFactory().WithFakeTime();
        _ = factory.CreateClient();
        var controller = factory.Services.GetRequiredService<ElevatorController>();

        var result = controller.RequestDestination(4);

        result.IsAccepted.ShouldBeTrue();
        await Eventually.HoldsAsync(
            () => controller.Elevator.Snapshot() is { CurrentFloor.Number: 4, State: ElevatorState.DoorOpen },
            "the car to open its door at floor 4",
            factory.Time,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ElevatorOptions_AreBoundFromTheElevatorSection()
    {
        await using var factory = new ApiFactory()
            .WithSetting("Elevator:TravelTimePerFloor", "00:00:02")
            .WithSetting("Elevator:DoorOpenDuration", "00:00:04")
            .WithSetting("Elevator:IntakeCapacity", "16");
        _ = factory.CreateClient();

        var options = factory.Services.GetRequiredService<ElevatorController>().Options;

        options.TravelTimePerFloor.ShouldBe(TimeSpan.FromSeconds(2));
        options.DoorOpenDuration.ShouldBe(TimeSpan.FromSeconds(4));
        options.IntakeCapacity.ShouldBe(16);
    }

    [Fact]
    public async Task ElevatorOptions_DefaultToProductionTimings()
    {
        await using var factory = new ApiFactory()
            .WithoutOverride("Elevator:TravelTimePerFloor")
            .WithoutOverride("Elevator:DoorOpenDuration");
        _ = factory.CreateClient();

        var options = factory.Services.GetRequiredService<ElevatorController>().Options;

        options.TravelTimePerFloor.ShouldBe(TimeSpan.FromSeconds(1.5));
        options.DoorOpenDuration.ShouldBe(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task InvalidElevatorOptions_StopTheHostFromStarting()
    {
        await using var factory = new ApiFactory().WithSetting("Elevator:TravelTimePerFloor", "00:00:00");

        var failure = Should.Throw<OptionsValidationException>(() => factory.CreateClient());

        failure.Message.ShouldContain("TravelTimePerFloor");
    }

    [Fact]
    public async Task Readiness_WhileTheControlLoopRuns_IsHealthy()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("Healthy");
    }

    [Fact]
    public async Task Readiness_WithoutTheControlLoop_IsUnhealthy()
    {
        await using var factory = new ApiFactory().WithoutControlLoop();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }
}
