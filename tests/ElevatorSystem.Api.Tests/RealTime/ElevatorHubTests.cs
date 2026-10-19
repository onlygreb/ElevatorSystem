using ElevatorSystem.Application;
using ElevatorSystem.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

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
        status.Status.ShouldBe(ElevatorStatus.Idle);
        status.PendingCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Connect_StatusUsesTheContractWireFormat()
    {
        await using var factory = new ApiFactory();
        await using var client = await HubClient.ConnectAsync(factory, captureRawStatus: true);

        var json = await client.NextRawStatusAsync();

        json.GetProperty("status").GetString().ShouldBe("Idle", "enums travel as names, never numbers");
        json.GetProperty("currentFloor").GetInt32().ShouldBe(1);
        json.TryGetProperty("CurrentFloor", out _).ShouldBeFalse("properties are camelCase");
    }

    [Fact]
    public async Task Request_IsNarratedAsActivity_AndFollowedByStatus()
    {
        await using var factory = new ApiFactory();
        await using var client = await HubClient.ConnectAsync(factory);
        var initial = await client.NextStatusAsync();
        var controller = factory.Services.GetRequiredService<ElevatorController>();

        var requestId = controller.RequestDestination(3).RequestId!.Value.Value;

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
    public async Task Hub_ExposesNoClientCallableMethods()
    {
        await using var factory = new ApiFactory();
        await using var client = await HubClient.ConnectAsync(factory);

        var attempt = () => client.Connection.InvokeAsync("RequestElevator", 3, "Up", TestContext.Current.CancellationToken);

        var refused = await attempt.ShouldThrowAsync<HubException>("commands only go through the REST API");
        refused.Message.ShouldContain("Method does not exist");
    }
}
