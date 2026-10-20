using System.Net;
using ElevatorSystem.Api.Tests.Staff;
using ElevatorSystem.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace ElevatorSystem.Api.Tests.RealTime;

public sealed class ElevatorHubTests
{
    [Fact]
    public async Task Connect_ReceivesTheCurrentStatusAtOnce()
    {
        await using var factory = new ApiFactory();
        await using var client = await HubClient.ConnectAsync(factory);

        var status = await client.NextStatusAsync();

        status.CurrentFloor.ShouldBe(1);
        status.Status.ShouldBeOneOf([ElevatorStatus.DoorOpen, ElevatorStatus.Idle], "the engine opens the door where it takes over, then closes it");
        status.PendingCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Connect_StatusUsesTheContractWireFormat()
    {
        await using var factory = new ApiFactory();
        await using var client = await HubClient.ConnectAsync(factory, captureRawStatus: true);

        var json = await client.NextRawStatusAsync();

        json.GetProperty("status").GetString().ShouldBeOneOf(["DoorOpen", "Idle"], "enums travel as names, never numbers");
        json.GetProperty("currentFloor").GetInt32().ShouldBe(1);
        json.TryGetProperty("CurrentFloor", out _).ShouldBeFalse("properties are camelCase");
    }

    [Fact]
    public async Task Request_IsNarratedAsActivity_AndFollowedByStatus()
    {
        await using var factory = new ApiFactory();
        await using var client = await HubClient.ConnectAsync(factory);
        using var http = factory.CreateClient();
        var initial = await client.NextStatusAsync();

        using var response = await http.PostJsonAsync(ApiRoutes.CarCalls, """{"floor":3}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var requestId = (await response.ReadJsonAsync()).GetProperty("requestId").GetGuid();
        var queued = await client.NextActivityAsync(activity => activity.Kind == ElevatorActivityKind.CallQueued);
        queued.RequestId.ShouldBe(requestId);
        queued.Call.ShouldBe(new CallDto(CallKind.Car, 3, null));
        var served = await client.NextActivityAsync(activity => activity.Kind == ElevatorActivityKind.CallServed);
        served.RequestId.ShouldBe(requestId);
        served.Floor.ShouldBe(3);
        var arrived = await client.NextStatusAsync(status => status.CurrentFloor == 3);
        arrived.Version.ShouldBeGreaterThan(initial.Version);
    }

    [Fact]
    public async Task Connect_AsStaff_ReceivesThePassengerCounts()
    {
        await using var factory = new ApiFactory();
        using var staff = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Auditor);
        await using var client = await HubClient.ConnectAsync(factory, cookieHeader: staff.CookieHeader);

        var occupancy = await client.NextOccupancyAsync();

        occupancy.Floors.Count.ShouldBe(10);
        occupancy.CarCapacity.ShouldBe(8);
    }

    [Fact]
    public async Task HallCall_ChangesTheCounts_AndTheyArePushedToStaff()
    {
        await using var factory = new ApiFactory();
        using var staff = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Admin);
        await using var client = await HubClient.ConnectAsync(factory, cookieHeader: staff.CookieHeader);
        using var http = factory.CreateClient();

        using var response = await http.PostJsonAsync(ApiRoutes.HallCalls, """{"floor":6,"direction":"Down"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await client.NextOccupancyAsync(occupancy => occupancy.Floors[5].Waiting == 1 || occupancy.PassengersInCar == 1);
    }

    [Fact]
    public async Task Anonymous_NeverReceivesThePassengerCounts()
    {
        await using var factory = new ApiFactory();
        await using var anonymous = await HubClient.ConnectAsync(factory);
        using var staff = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Admin);
        await using var staffHub = await HubClient.ConnectAsync(factory, cookieHeader: staff.CookieHeader);
        using var http = factory.CreateClient();

        using var response = await http.PostJsonAsync(ApiRoutes.HallCalls, """{"floor":4,"direction":"Up"}""");

        await staffHub.NextOccupancyAsync(occupancy => occupancy.Floors[3].Waiting == 1 || occupancy.PassengersInCar == 1);
        await anonymous.NextActivityAsync(activity => activity.Kind == ElevatorActivityKind.CallServed);
        await Should.ThrowAsync<TimeoutException>(() => anonymous.NextOccupancyAsync(), "counts are for staff only (ADR-0022)");
    }

    [Fact]
    public async Task Connect_WithAPasswordOnlySession_ReceivesNoCounts()
    {
        await using var factory = new ApiFactory();
        using var staff = await StaffClient.SignedInWithPasswordAsync(factory, StaffAccounts.Admin);
        await using var client = await HubClient.ConnectAsync(factory, cookieHeader: staff.CookieHeader);

        await client.NextStatusAsync();
        await Should.ThrowAsync<TimeoutException>(() => client.NextOccupancyAsync(), "every staff capability needs the second factor");
    }

    [Fact]
    public async Task Hub_ExposesNoClientCallableMethods()
    {
        await using var factory = new ApiFactory();
        await using var client = await HubClient.ConnectAsync(factory);

        var attempt = () => client.Connection.InvokeAsync("RequestElevator", 3, "Up", TestContext.Current.CancellationToken);

        var refused = await attempt.ShouldThrowAsync<HubException>("commands only go through the REST API");
        refused.Message.ShouldContain("Method does not exist");
    }
}
