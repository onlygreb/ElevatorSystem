using System.Net;
using ElevatorSystem.Api.Tests.RealTime;
using ElevatorSystem.Contracts;

namespace ElevatorSystem.Api.Tests.Staff;

/// <summary>Admins start and stop the demo scenarios (ADR-0027); simulated passengers then appear like everyone else.</summary>
public sealed class ScenarioEndpointTests
{
    [Fact]
    public async Task StartingAScenario_BringsSimulatedPassengers_AndIsAudited()
    {
        await using var factory = new ApiFactory().WithSetting("Simulation:Enabled", "true");
        using var admin = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Admin);
        await using var hub = await HubClient.ConnectAsync(factory);

        using var response = await admin.PostAsync(ApiRoutes.Scenarios, new StartScenarioRequest(DemoScenario.MorningRush));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var joined = await hub.NextActivityAsync(activity => activity.Kind == ElevatorActivityKind.PassengerJoined);
        joined.Simulated.ShouldBe(true);
        joined.Floor.ShouldBe(1, "every simulated passenger arrives on the lowest floor");
        var entry = (await AuditTrailEndpointTests.EntriesAsync(admin)).First(entry => entry.Action == "elevator.demo-scenario");
        entry.Target.ShouldStartWith("MorningRush command ");
        entry.Actor.ShouldBe(StaffAccounts.Admin.Email);
    }

    [Fact]
    public async Task Stopping_RemovesTheSimulatedPassengers()
    {
        await using var factory = new ApiFactory().WithSetting("Simulation:Enabled", "true");
        using var admin = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Admin);
        await using var hub = await HubClient.ConnectAsync(factory);
        using (await admin.PostAsync(ApiRoutes.Scenarios, new StartScenarioRequest(DemoScenario.BusyDay)))
        {
        }

        await hub.NextActivityAsync(activity => activity.Kind == ElevatorActivityKind.PassengerJoined);

        using var stopped = await admin.PostAsync(ApiRoutes.ScenariosStop);

        stopped.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var removed = await hub.NextActivityAsync(activity => activity.Kind == ElevatorActivityKind.PassengerRemoved);
        removed.Simulated.ShouldBe(true);
    }

    [Fact]
    public async Task WhenDisabled_StartingIsAConflict()
    {
        await using var factory = new ApiFactory().WithSetting("Simulation:Enabled", "false");
        using var admin = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Admin);

        using var response = await admin.PostAsync(ApiRoutes.Scenarios, new StartScenarioRequest(DemoScenario.LunchBreak));

        await response.ShouldBeProblemAsync(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AnUnknownScenario_IsRefused()
    {
        await using var factory = new ApiFactory().WithSetting("Simulation:Enabled", "true");
        using var admin = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Admin);

        using var response = await admin.PostAsync(ApiRoutes.Scenarios, System.Text.Json.JsonDocument.Parse("""{"scenario":99}""").RootElement);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
