using System.Net;
using ElevatorSystem.Api.Tests.RealTime;
using ElevatorSystem.Contracts;

namespace ElevatorSystem.Api.Tests.Staff;

/// <summary>Admins switch between FIFO and LOOK, only while the car is out of service (ADR-0033).</summary>
public sealed class SchedulingEndpointTests
{
    [Fact]
    public async Task InService_SwitchingIsAConflict_AndNothingIsAudited()
    {
        await using var factory = new ApiFactory();
        using var admin = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Admin);
        await factory.StatusAsync();

        using var response = await admin.PostAsync(ApiRoutes.Scheduling, new SchedulingRequest(Scheduling.Look));

        await response.ShouldBeProblemAsync(HttpStatusCode.Conflict);
        (await AuditTrailEndpointTests.EntriesAsync(admin)).ShouldNotContain(entry => entry.Action == "elevator.scheduling");
    }

    [Fact]
    public async Task OutOfService_SwitchingIsAudited_AndTheEngineSaysSo()
    {
        await using var factory = new ApiFactory();
        using var admin = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Admin);
        await using var hub = await HubClient.ConnectAsync(factory);
        using (await admin.PostAsync(ApiRoutes.ServiceActions, new ServiceActionRequest(ServiceAction.StartMaintenance)))
        {
        }

        await hub.NextActivityAsync(activity => activity.Kind == ElevatorActivityKind.OutOfService);

        using var response = await admin.PostAsync(ApiRoutes.Scheduling, new SchedulingRequest(Scheduling.Look));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var switched = await hub.NextActivityAsync(activity => activity.Kind == ElevatorActivityKind.SchedulingChanged);
        switched.Scheduling.ShouldBe(Scheduling.Look);
        var entry = (await AuditTrailEndpointTests.EntriesAsync(admin)).First(entry => entry.Action == "elevator.scheduling");
        entry.Target.ShouldStartWith("Look command ");
        entry.Actor.ShouldBe(StaffAccounts.Admin.Email);
    }

    [Fact]
    public async Task AnUnknownScheduling_IsRefused()
    {
        await using var factory = new ApiFactory();
        using var admin = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Admin);

        using var response = await admin.PostAsync(ApiRoutes.Scheduling, System.Text.Json.JsonDocument.Parse("""{"scheduling":9}""").RootElement);

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity);
    }
}
