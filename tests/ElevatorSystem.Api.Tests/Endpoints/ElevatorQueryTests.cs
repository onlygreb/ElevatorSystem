using System.Net;
using ElevatorSystem.Application;
using ElevatorSystem.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace ElevatorSystem.Api.Tests.Endpoints;

/// <summary>GET elevator and GET configuration, including the exact wire format.</summary>
public sealed class ElevatorQueryTests
{
    [Fact]
    public async Task GetStatus_IdleElevator_ReturnsItsSnapshot()
    {
        await using var factory = new ApiFactory().WithFakeTime();
        using var client = factory.CreateClient();

        using var response = await client.GetRouteAsync(ApiRoutes.Elevator);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var status = await response.ReadJsonAsync();
        status.GetProperty("currentFloor").GetInt32().ShouldBe(1);
        status.GetProperty("status").GetString().ShouldBe("Idle", "enums travel as names");
        status.GetProperty("travelDirection").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        status.GetProperty("pendingCalls").GetArrayLength().ShouldBe(0);
        status.GetProperty("version").GetInt64().ShouldBe(0);
        status.GetProperty("takenAt").GetDateTimeOffset().ShouldBe(ApiFactory.StartTime);
    }

    [Fact]
    public async Task GetStatus_AfterARequest_ShowsThePendingCallAndTheTrip()
    {
        await using var factory = new ApiFactory().WithFakeTime();
        using var client = factory.CreateClient();
        using var accepted = await client.PostJsonAsync(ApiRoutes.CarCalls, """{"floor":5}""");
        var controller = factory.Services.GetRequiredService<ElevatorController>();
        await Eventually.HoldsAsync(() => controller.Elevator.Snapshot().PendingCalls.Length == 1, "the call to be queued", cancellationToken: TestContext.Current.CancellationToken);

        using var response = await client.GetRouteAsync(ApiRoutes.Elevator);

        var status = await response.ReadJsonAsync();
        status.GetProperty("status").GetString().ShouldBe(nameof(ElevatorStatus.MovingUp));
        status.GetProperty("travelDirection").GetString().ShouldBe(nameof(CallDirection.Up));
        var pending = status.GetProperty("pendingCalls")[0];
        pending.GetProperty("call").GetProperty("kind").GetString().ShouldBe(nameof(CallKind.Car));
        pending.GetProperty("call").GetProperty("floor").GetInt32().ShouldBe(5);
        pending.GetProperty("requestCount").GetInt32().ShouldBe(1);
        status.GetProperty("targetFloors").EnumerateArray().Select(floor => floor.GetInt32()).ShouldBe([5]);
    }

    [Fact]
    public async Task GetConfiguration_ReturnsTheBuildingAndTheConfiguredTimings()
    {
        await using var factory = new ApiFactory()
            .WithSetting("Elevator:TravelTimePerFloor", "00:00:01.5")
            .WithSetting("Elevator:DoorOpenDuration", "00:00:03");
        using var client = factory.CreateClient();

        using var response = await client.GetRouteAsync(ApiRoutes.Configuration);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var configuration = await response.ReadJsonAsync();
        configuration.GetProperty("lowestFloor").GetInt32().ShouldBe(1);
        configuration.GetProperty("highestFloor").GetInt32().ShouldBe(10);
        configuration.GetProperty("travelTimePerFloor").GetString().ShouldBe("00:00:01.5000000");
        configuration.GetProperty("doorOpenDuration").GetString().ShouldBe("00:00:03");
        configuration.GetProperty("schedulingStrategy").GetString().ShouldBe("FIFO");
    }
}
