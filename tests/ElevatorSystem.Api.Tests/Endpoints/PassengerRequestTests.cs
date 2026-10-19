using System.Net;
using ElevatorSystem.Application;
using ElevatorSystem.Contracts;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ElevatorSystem.Api.Tests.Endpoints;

/// <summary>POST hall-calls and car-calls: acceptance and every refusal, with exact statuses and reasons.</summary>
public sealed class PassengerRequestTests(ApiFactory factory) : IClassFixture<ApiFactory>, IDisposable
{
    private readonly HttpClient _client = factory.CreateClient();

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task RequestElevator_ValidCall_Returns202WithTheRequestId()
    {
        using var response = await _client.PostJsonAsync(ApiRoutes.HallCalls, """{"floor":5,"direction":"Down"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await response.ReadJsonAsync();
        Guid.Parse(body.GetProperty("requestId").GetString()!).ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task RequestDestination_ValidCall_Returns202WithTheRequestId()
    {
        using var response = await _client.PostJsonAsync(ApiRoutes.CarCalls, """{"floor":7}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await response.ReadJsonAsync();
        Guid.Parse(body.GetProperty("requestId").GetString()!).ShouldNotBe(Guid.Empty);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(-3)]
    [InlineData(int.MaxValue)]
    public async Task RequestElevator_FloorOutsideTheBuilding_Returns400InvalidFloor(int floor)
    {
        using var response = await _client.PostJsonAsync(ApiRoutes.HallCalls, $$"""{"floor":{{floor}},"direction":"Up"}""");

        var problem = await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest);
        problem.Reason().ShouldBe(nameof(RejectionReason.InvalidFloor));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public async Task RequestDestination_FloorOutsideTheBuilding_Returns400InvalidFloor(int floor)
    {
        using var response = await _client.PostJsonAsync(ApiRoutes.CarCalls, $$"""{"floor":{{floor}}}""");

        var problem = await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest);
        problem.Reason().ShouldBe(nameof(RejectionReason.InvalidFloor));
    }

    [Theory]
    [InlineData(10, "Up")]
    [InlineData(1, "Down")]
    public async Task RequestElevator_NoTravelThatWay_Returns400InvalidDirection(int floor, string direction)
    {
        using var response = await _client.PostJsonAsync(ApiRoutes.HallCalls, $$"""{"floor":{{floor}},"direction":"{{direction}}"}""");

        var problem = await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest);
        problem.Reason().ShouldBe(nameof(RejectionReason.InvalidDirection));
    }

    [Fact]
    public async Task RequestElevator_UndefinedNumericDirection_Returns400InvalidDirection()
    {
        using var response = await _client.PostJsonAsync(ApiRoutes.HallCalls, """{"floor":3,"direction":7}""");

        var problem = await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest);
        problem.Reason().ShouldBe(nameof(RejectionReason.InvalidDirection));
    }

    [Theory]
    [InlineData("""{"floor":3,"direction":"Sideways"}""")]
    [InlineData("""{"floor":3,"direction":"up down"}""")]
    [InlineData("""{"floor":3,"direction":null}""")]
    [InlineData("""{"floor":3,"direction":{}}""")]
    public async Task RequestElevator_UnknownDirectionValue_Returns400NotA500(string json)
    {
        using var response = await _client.PostJsonAsync(ApiRoutes.HallCalls, json);

        var problem = await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest);
        problem.Reason().ShouldBeNull("the body never reached the elevator");
    }

    [Theory]
    [InlineData("""{"floor":3}""")]
    [InlineData("""{"direction":"Up"}""")]
    [InlineData("{}")]
    public async Task RequestElevator_MissingField_Returns400(string json)
    {
        using var response = await _client.PostJsonAsync(ApiRoutes.HallCalls, json);

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(ApiRoutes.HallCalls, "{")]
    [InlineData(ApiRoutes.HallCalls, "not json")]
    [InlineData(ApiRoutes.HallCalls, "[]")]
    [InlineData(ApiRoutes.HallCalls, """{"floor":"three","direction":"Up"}""")]
    [InlineData(ApiRoutes.HallCalls, """{"floor":"3","direction":"Up"}""")]
    [InlineData(ApiRoutes.HallCalls, "")]
    [InlineData(ApiRoutes.CarCalls, """{"floor":3.5}""")]
    [InlineData(ApiRoutes.CarCalls, "null")]
    [InlineData(ApiRoutes.CarCalls, "")]
    public async Task Request_MalformedBody_Returns400(string route, string json)
    {
        using var response = await _client.PostJsonAsync(route, json);

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Request_NotJson_Returns415()
    {
        using var response = await _client.PostJsonAsync(ApiRoutes.CarCalls, "floor=3", "application/x-www-form-urlencoded");

        await response.ShouldBeProblemAsync(HttpStatusCode.UnsupportedMediaType);
    }

    [Theory]
    [InlineData(ApiRoutes.HallCalls)]
    [InlineData(ApiRoutes.CarCalls)]
    public void Request_BodySize_IsCappedAtOneKilobyte(string route)
    {
        // Kestrel enforces this limit (413) while reading the body; the in-memory test server does not
        // implement the limit, so the test checks that every passenger endpoint declares it.
        var endpoint = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText == route);

        var limit = endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>();

        limit.ShouldNotBeNull().MaxRequestBodySize.ShouldBe(1024);
    }

    [Theory]
    [InlineData(ApiRoutes.HallCalls, """{"floor":3,"direction":"Up"}""")]
    [InlineData(ApiRoutes.CarCalls, """{"floor":3}""")]
    public async Task Request_WhileOutOfService_Returns409OutOfService(string route, string json)
    {
        await using var outOfService = new ApiFactory();
        using var client = outOfService.CreateClient();
        outOfService.Services.GetRequiredService<ElevatorController>().StartMaintenance();

        using var response = await client.PostJsonAsync(route, json);

        var problem = await response.ShouldBeProblemAsync(HttpStatusCode.Conflict);
        problem.Reason().ShouldBe(nameof(RejectionReason.OutOfService));
    }

    [Theory]
    [InlineData(ApiRoutes.HallCalls, """{"floor":3,"direction":"Up"}""")]
    [InlineData(ApiRoutes.CarCalls, """{"floor":3}""")]
    public async Task Request_WhenTheIntakeIsFull_Returns503BusyWithRetryAfter(string route, string json)
    {
        await using var stalled = new ApiFactory().WithoutControlLoop().WithSetting("Elevator:IntakeCapacity", "1");
        using var client = stalled.CreateClient();
        using var first = await client.PostJsonAsync(route, json);
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        using var response = await client.PostJsonAsync(route, json);

        var problem = await response.ShouldBeProblemAsync(HttpStatusCode.ServiceUnavailable);
        problem.Reason().ShouldBe(nameof(RejectionReason.Busy));
        response.RetryAfter().ShouldBe("1");
    }
}
