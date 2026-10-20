using System.Net;
using ElevatorSystem.Api.Hosting;
using ElevatorSystem.Application;
using ElevatorSystem.Application.Hosting;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using ElevatorSystem.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ElevatorSystem.Api.Tests;

public sealed class HostingTests
{
    [Fact]
    public async Task SingleProcess_RunsTheEngineInTheHost_AndServesRequests()
    {
        await using var factory = new ApiFactory().WithFakeTime();
        using var client = factory.CreateClient();
        var controller = await factory.ControllerAsync();

        using var response = await client.PostJsonAsync(ApiRoutes.CarCalls, """{"floor":4}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await Eventually.HoldsAsync(
            () => controller.Elevator.Snapshot() is { CurrentFloor.Number: 4, State: ElevatorState.DoorOpen },
            "the car to open its door at floor 4",
            factory.Time,
            cancellationToken: TestContext.Current.CancellationToken);
        await factory.StatusAsync(status => status is { CurrentFloor: 4, Status: ElevatorStatus.DoorOpen });
    }

    [Fact]
    public async Task Distributed_IsTheDefault_AndUsesRabbitMq()
    {
        await using var factory = new ApiFactory()
            .WithoutOverride("Hosting:Mode")
            .InEnvironment("Production")
            .WithSetting("ConnectionStrings:messaging", "amqp://api:secret@127.0.0.1:1/")
            .WithSetting("ConnectionStrings:elevatordb", "Host=127.0.0.1;Port=1;Database=elevatordb;Username=api;Password=secret");
        _ = factory.Server;

        factory.Services.GetRequiredService<IOptions<HostingOptions>>().Value.Mode.ShouldBe(HostingMode.Distributed);
        factory.Services.GetRequiredService<IElevatorCommandPublisher>().ShouldBeOfType<RabbitMqElevatorCommandPublisher>();
        factory.Services.GetService<ElevatorEngine>().ShouldBeNull("in distributed mode the engine runs in its own processes");
        factory.Services.GetRequiredService<IOptions<MessagingOptions>>().Value.Uri.ShouldBe(new Uri("amqp://api:secret@127.0.0.1:1/"));
    }

    [Fact]
    public async Task ElevatorOptions_AreBoundFromTheElevatorSection()
    {
        await using var factory = new ApiFactory()
            .WithSetting("Elevator:TravelTimePerFloor", "00:00:02")
            .WithSetting("Elevator:DoorOpenDuration", "00:00:04")
            .WithSetting("Elevator:IntakeCapacity", "16");

        var options = (await factory.ControllerAsync()).Options;

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
        _ = factory.Server;

        var options = factory.Services.GetRequiredService<ElevatorControllerOptions>();

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
    public async Task Readiness_OnceTheEngineReported_IsHealthy()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        await factory.StatusAsync();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("Healthy");
    }

    [Fact]
    public async Task Readiness_WithoutTheEngine_IsUnhealthy()
    {
        await using var factory = new ApiFactory().WithoutEngine();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }
}
