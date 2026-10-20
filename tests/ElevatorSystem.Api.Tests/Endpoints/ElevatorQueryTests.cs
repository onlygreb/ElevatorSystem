using System.Net;
using ElevatorSystem.Api.Tests.Staff;
using ElevatorSystem.Contracts;

namespace ElevatorSystem.Api.Tests.Endpoints;

/// <summary>GET elevator, GET configuration and GET occupancy, including the exact wire format.</summary>
public sealed class ElevatorQueryTests
{
    [Fact]
    public async Task GetStatus_AfterStartUp_ReturnsTheStatusTheEngineReported()
    {
        await using var factory = new ApiFactory().WithFakeTime();
        using var client = factory.CreateClient();
        var reported = await factory.StatusAsync();

        using var response = await client.GetRouteAsync(ApiRoutes.Elevator);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var status = await response.ReadJsonAsync();
        status.GetProperty("currentFloor").GetInt32().ShouldBe(1);
        status.GetProperty("status").GetString().ShouldBe("DoorOpen", "enums travel as names; the engine opens the door when it takes over");
        status.GetProperty("travelDirection").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        status.GetProperty("pendingCalls").GetArrayLength().ShouldBe(0);
        status.GetProperty("version").GetInt64().ShouldBe(reported.Version);
        status.GetProperty("takenAt").GetDateTimeOffset().ShouldBe(ApiFactory.StartTime);
    }

    [Fact]
    public async Task GetStatus_AfterARequest_ShowsThePendingCallAndTheTrip()
    {
        await using var factory = new ApiFactory().WithFakeTime();
        using var client = factory.CreateClient();
        await factory.StatusAsync(status => status.Status == ElevatorStatus.DoorOpen);
        using var accepted = await client.PostJsonAsync(ApiRoutes.CarCalls, """{"floor":5}""");
        // The car waits open at floor 1; the call starts the door countdown, which then has to run out.
        await factory.StatusAsync(status => status.PendingCalls.Count == 1 && status.DoorsCloseAt is not null);
        factory.Time!.Advance(ApiFactory.DoorTime);
        await factory.StatusAsync(status => status.PendingCalls.Count == 1 && status.Status == ElevatorStatus.MovingUp);

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
    public async Task GetStatus_WhileTheCarWaitsOpen_HasNoCountdown_AndACallStartsOne()
    {
        await using var factory = new ApiFactory().WithFakeTime();
        using var client = factory.CreateClient();
        var waiting = await factory.StatusAsync(status => status.Status == ElevatorStatus.DoorOpen);

        using var accepted = await client.PostJsonAsync(ApiRoutes.HallCalls, """{"floor":6,"direction":"Down"}""");
        var counting = await factory.StatusAsync(status => status.DoorsCloseAt is not null);

        waiting.DoorsCloseAt.ShouldBeNull("with nowhere to go the door stays open, so there is nothing to count down");
        counting.Status.ShouldBe(ElevatorStatus.DoorOpen);
        counting.DoorsCloseAt.ShouldBe(factory.Time!.GetUtcNow() + ApiFactory.DoorTime);
        using var response = await client.GetRouteAsync(ApiRoutes.Elevator);
        (await response.ReadJsonAsync()).GetProperty("doorsCloseAt").GetDateTimeOffset().ShouldBe(counting.DoorsCloseAt!.Value);
    }

    [Fact]
    public async Task GetStatus_BeforeTheEngineReported_Returns503WithRetryAfter()
    {
        await using var factory = new ApiFactory().WithoutEngine();
        using var client = factory.CreateClient();

        using var response = await client.GetRouteAsync(ApiRoutes.Elevator);

        var problem = await response.ShouldBeProblemAsync(HttpStatusCode.ServiceUnavailable);
        problem.Reason().ShouldBeNull("nothing was refused; the state is simply not known yet");
        response.RetryAfter().ShouldBe("2");
    }

    [Fact]
    public async Task GetOccupancy_AsStaff_ReturnsTheCountsPerFloor()
    {
        await using var factory = new ApiFactory();
        using var staff = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Auditor);
        var client = staff.Http;
        await factory.StatusAsync();

        using var response = await Eventually.ReturnsAsync(
            async () => await client.GetRouteAsync(ApiRoutes.Occupancy) is { StatusCode: HttpStatusCode.OK } ok ? ok : null,
            "the occupancy to be known");

        var occupancy = await response.ReadJsonAsync();
        occupancy.GetProperty("passengersInCar").GetInt32().ShouldBe(0);
        occupancy.GetProperty("carCapacity").GetInt32().ShouldBe(8);
        occupancy.GetProperty("floors").GetArrayLength().ShouldBe(10);
        occupancy.GetProperty("floors")[0].GetProperty("floor").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task GetOccupancy_BeforeTheEngineReported_Returns503WithRetryAfter()
    {
        await using var factory = new ApiFactory().WithoutEngine();
        using var staff = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Admin);
        var client = staff.Http;

        using var response = await client.GetRouteAsync(ApiRoutes.Occupancy);

        await response.ShouldBeProblemAsync(HttpStatusCode.ServiceUnavailable);
        response.RetryAfter().ShouldBe("2");
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

    [Fact]
    public async Task GetConfiguration_WithLookConfigured_ReportsLook()
    {
        await using var factory = new ApiFactory().WithSetting("Elevator:Scheduling", "Look");
        using var client = factory.CreateClient();

        using var response = await client.GetRouteAsync(ApiRoutes.Configuration);

        (await response.ReadJsonAsync()).GetProperty("schedulingStrategy").GetString().ShouldBe("LOOK");
    }
}
