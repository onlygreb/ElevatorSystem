using System.Net;
using System.Text;
using ElevatorSystem.Api.Security;
using ElevatorSystem.Contracts;
using Microsoft.AspNetCore.Http;

namespace ElevatorSystem.Api.Tests.Security;

public sealed class RateLimitingTests
{
    private const string CarCall = """{"floor":4}""";

    [Fact]
    public async Task PassengerRequests_BeyondTheBurst_Return429WithRetryAfter()
    {
        await using var factory = WithPassengerLimit(2);
        using var client = factory.CreateClient();

        using var first = await PostFromAsync(client, "203.0.113.10");
        using var second = await PostFromAsync(client, "203.0.113.10");
        using var third = await PostFromAsync(client, "203.0.113.10");

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        second.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await third.ShouldBeProblemAsync(HttpStatusCode.TooManyRequests);
        third.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldNotBeNull().TotalSeconds.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task PassengerLimit_IsCountedPerClientAddress()
    {
        await using var factory = WithPassengerLimit(1);
        using var client = factory.CreateClient();
        using var exhausted = await PostFromAsync(client, "203.0.113.10");
        using var refused = await PostFromAsync(client, "203.0.113.10");
        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        using var otherClient = await PostFromAsync(client, "198.51.100.20");

        otherClient.StatusCode.ShouldBe(HttpStatusCode.Accepted, "one client's flood must not lock out everyone else");
    }

    [Fact]
    public async Task PassengerLimit_GroupsIpv6AddressesByTheirSlash64()
    {
        await using var factory = WithPassengerLimit(1);
        using var client = factory.CreateClient();
        using var exhausted = await PostFromAsync(client, "2001:db8:1:2::1");

        using var sameNetwork = await PostFromAsync(client, "2001:db8:1:2:ffff::99");
        using var otherNetwork = await PostFromAsync(client, "2001:db8:1:3::1");

        sameNetwork.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "rotating addresses inside one /64 buys nothing");
        otherNetwork.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task ReadLimit_IsSeparateFromThePassengerLimit()
    {
        await using var factory = WithPassengerLimit(1);
        using var client = factory.CreateClient();
        using var exhausted = await PostFromAsync(client, "203.0.113.10");

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiRoutes.Elevator, UriKind.Relative));
        request.Headers.Add(ApiFactory.ClientAddressHeader, "203.0.113.10");
        using var read = await client.SendAsync(request, Http.Cancellation);

        read.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ReadRequests_BeyondTheBurst_Return429()
    {
        await using var factory = new ApiFactory()
            .WithSetting("RateLimiting:Read:TokenLimit", "1")
            .WithSetting("RateLimiting:Read:TokensPerPeriod", "1")
            .WithSetting("RateLimiting:Read:ReplenishmentPeriod", "01:00:00");
        using var client = factory.CreateClient();
        using var first = await client.GetRouteAsync(ApiRoutes.Configuration);

        using var second = await client.GetRouteAsync(ApiRoutes.Configuration);

        await second.ShouldBeProblemAsync(HttpStatusCode.TooManyRequests);
        second.Headers.RetryAfter.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:aa:bb:cc:dd:ee:ff", "2001:db8:aa:bb::")]
    public void PartitionKey_NormalisesTheClientAddress(string address, string expected)
    {
        var key = RateLimiting.PartitionKey(IPAddress.Parse(address));

        key.ShouldBe(IPAddress.Parse(expected));
    }

    [Fact]
    public void PartitionKey_WithoutAnAddress_UsesOneSharedKey()
    {
        RateLimiting.PartitionKey(null).ShouldBe(IPAddress.None);
    }

    [Fact]
    public void GlobalLimiter_CapsConcurrentApiRequests_WithoutQueueing()
    {
        using var limiter = RateLimiting.CreateGlobalLimiter(permitLimit: 1);
        var api = Request("/api/v1/elevator");

        using var inFlight = limiter.AttemptAcquire(api);
        using var refused = limiter.AttemptAcquire(Request("/api/v1/car-calls"));
        inFlight.IsAcquired.ShouldBeTrue();
        refused.IsAcquired.ShouldBeFalse("the second concurrent API request is refused, not queued");

        inFlight.Dispose();
        using var afterwards = limiter.AttemptAcquire(api);

        afterwards.IsAcquired.ShouldBeTrue("the permit returns when the first request completes");
    }

    [Fact]
    public void GlobalLimiter_LeavesPagesAndStaticFilesAlone()
    {
        using var limiter = RateLimiting.CreateGlobalLimiter(permitLimit: 1);
        using var api = limiter.AttemptAcquire(Request("/api/v1/elevator"));

        using var page = limiter.AttemptAcquire(Request("/"));
        using var asset = limiter.AttemptAcquire(Request("/app.css"));

        page.IsAcquired.ShouldBeTrue();
        asset.IsAcquired.ShouldBeTrue();
    }

    private static ApiFactory WithPassengerLimit(int burst) => new ApiFactory()
        .WithSetting("RateLimiting:Passenger:TokenLimit", burst.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .WithSetting("RateLimiting:Passenger:TokensPerPeriod", "1")
        .WithSetting("RateLimiting:Passenger:ReplenishmentPeriod", "01:00:00");

    private static Task<HttpResponseMessage> PostFromAsync(HttpClient client, string address)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.CarCalls, UriKind.Relative))
        {
            Content = new StringContent(CarCall, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(ApiFactory.ClientAddressHeader, address);
        return client.SendAsync(request, Http.Cancellation);
    }

    private static DefaultHttpContext Request(string path) => new() { Request = { Path = path } };
}
