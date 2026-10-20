using System.Net;
using System.Net.Http.Json;
using ElevatorSystem.Contracts;

namespace ElevatorSystem.Api.Tests.Staff;

/// <summary>Admin service actions: their effect on the car, the audit trail, idempotency and refusals (ADR-0017, ADR-0022).</summary>
public sealed class ServiceActionTests
{
    [Fact]
    public async Task EmergencyStop_StopsTheCar_AndIsAuditedWithTheAdmin()
    {
        await using var factory = new ApiFactory();
        using var admin = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Admin);

        using var response = await admin.PostAsync(ApiRoutes.ServiceActions, new ServiceActionRequest(ServiceAction.EmergencyStop));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var accepted = (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.ServiceActionAcceptedResponse, Http.Cancellation))!;
        accepted.Action.ShouldBe(ServiceAction.EmergencyStop);
        var status = await factory.StatusAsync(status => status.Status == ElevatorStatus.OutOfService);
        status.OutOfServiceCause.ShouldBe(OutOfServiceCause.EmergencyStop);
        var entry = (await AuditTrailEndpointTests.EntriesAsync(admin)).First(entry => entry.Action == "elevator.service-action");
        entry.Target.ShouldBe($"EmergencyStop command {accepted.CommandId}");
        entry.Outcome.ShouldBe("requested");
        entry.Actor.ShouldBe(StaffAccounts.Admin.Email);
    }

    [Fact]
    public async Task Maintenance_ThenReturnToService_PutsTheCarBackInService()
    {
        await using var factory = new ApiFactory();
        using var admin = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Admin);

        using var maintenance = await admin.PostAsync(ApiRoutes.ServiceActions, new ServiceActionRequest(ServiceAction.StartMaintenance));
        var stopped = await factory.StatusAsync(status => status.Status == ElevatorStatus.OutOfService);
        using var resumed = await admin.PostAsync(ApiRoutes.ServiceActions, new ServiceActionRequest(ServiceAction.ReturnToService));

        maintenance.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        stopped.OutOfServiceCause.ShouldBe(OutOfServiceCause.Maintenance);
        resumed.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await factory.StatusAsync(status => status.Status != ElevatorStatus.OutOfService);
    }

    [Fact]
    public async Task SameIdempotencyKey_ReturnsTheFirstAnswer_AndActsOnce()
    {
        await using var factory = new ApiFactory();
        using var admin = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Admin);
        var key = new Dictionary<string, string> { [ApiRoutes.IdempotencyKeyHeader] = Guid.NewGuid().ToString() };

        using var first = await admin.PostAsync(ApiRoutes.ServiceActions, new ServiceActionRequest(ServiceAction.StartMaintenance), headers: key);
        using var retry = await admin.PostAsync(ApiRoutes.ServiceActions, new ServiceActionRequest(ServiceAction.StartMaintenance), headers: key);

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        retry.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var firstId = (await first.Content.ReadFromJsonAsync(ContractsJsonContext.Default.ServiceActionAcceptedResponse, Http.Cancellation))!.CommandId;
        var retryId = (await retry.Content.ReadFromJsonAsync(ContractsJsonContext.Default.ServiceActionAcceptedResponse, Http.Cancellation))!.CommandId;
        retryId.ShouldBe(firstId);
        (await AuditTrailEndpointTests.EntriesAsync(admin)).Count(entry => entry.Action == "elevator.service-action").ShouldBe(1);
    }

    [Fact]
    public async Task SameIdempotencyKey_ForAnotherAction_IsRefused()
    {
        await using var factory = new ApiFactory();
        using var admin = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Admin);
        var key = new Dictionary<string, string> { [ApiRoutes.IdempotencyKeyHeader] = Guid.NewGuid().ToString() };

        using var first = await admin.PostAsync(ApiRoutes.ServiceActions, new ServiceActionRequest(ServiceAction.StartMaintenance), headers: key);
        using var other = await admin.PostAsync(ApiRoutes.ServiceActions, new ServiceActionRequest(ServiceAction.ReturnToService), headers: key);

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await other.ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task IdempotencyKey_ThatIsNotAUuid_IsRefused()
    {
        await using var factory = new ApiFactory();
        using var admin = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Admin);

        using var response = await admin.PostAsync(
            ApiRoutes.ServiceActions,
            new ServiceActionRequest(ServiceAction.EmergencyStop),
            headers: new Dictionary<string, string> { [ApiRoutes.IdempotencyKeyHeader] = "retry-1" });

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("""{"action":"OpenDoors"}""")]
    [InlineData("""{"action":7}""")]
    [InlineData("""{}""")]
    public async Task UnknownOrMissingAction_IsRefused(string json)
    {
        await using var factory = new ApiFactory();
        using var admin = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Admin);

        using var response = await admin.PostAsync(ApiRoutes.ServiceActions, System.Text.Json.JsonDocument.Parse(json).RootElement);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AuditTrailEndpointTests.EntriesAsync(admin)).ShouldNotContain(entry => entry.Action == "elevator.service-action");
    }

    [Fact]
    public async Task WithoutTheAntiforgeryToken_IsRefusedAndNothingHappens()
    {
        await using var factory = new ApiFactory();
        using var admin = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Admin);

        using var response = await admin.PostAsync(ApiRoutes.ServiceActions, new ServiceActionRequest(ServiceAction.EmergencyStop), antiforgery: false);

        (await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest)).Reason().ShouldBe("Antiforgery");
        (await AuditTrailEndpointTests.EntriesAsync(admin)).ShouldNotContain(entry => entry.Action == "elevator.service-action");
    }

    [Fact]
    public async Task WhenTheCommandCannotBeSent_Returns503_AndTheRefusalIsAudited()
    {
        await using var factory = new ApiFactory().WithoutEngine().WithSetting("Hosting:InMemoryCommandCapacity", "1");
        using var admin = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Admin);
        using (var filler = await admin.PostAsync(ApiRoutes.ServiceActions, new ServiceActionRequest(ServiceAction.StartMaintenance)))
        {
            filler.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        using var response = await admin.PostAsync(ApiRoutes.ServiceActions, new ServiceActionRequest(ServiceAction.EmergencyStop));

        (await response.ShouldBeProblemAsync(HttpStatusCode.ServiceUnavailable)).Reason().ShouldBe(nameof(RejectionReason.Busy));
        response.RetryAfter().ShouldBe("2");
        var outcomes = (await AuditTrailEndpointTests.EntriesAsync(admin))
            .Where(entry => entry.Target.StartsWith("EmergencyStop", StringComparison.Ordinal))
            .Select(entry => entry.Outcome);
        outcomes.ShouldBe(["refused", "requested"]);
    }
}
