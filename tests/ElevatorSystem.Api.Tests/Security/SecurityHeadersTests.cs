using System.Net;
using ElevatorSystem.Contracts;
using Microsoft.Extensions.Hosting;

namespace ElevatorSystem.Api.Tests.Security;

/// <summary>Every kind of response carries the hardening headers, including errors and refusals.</summary>
public sealed class SecurityHeadersTests
{
    private const string ExpectedPolicy =
        "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; "
        + "font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

    public static TheoryData<string, string> Responses => new()
    {
        { "GET", ApiRoutes.Elevator },
        { "GET", ApiRoutes.Configuration },
        { "POST", ApiRoutes.CarCalls },
        { "GET", "/api/v1/missing" },
        { "GET", "/" },
        { "GET", "/no-such-page" },
        { "GET", "/app.css" },
        { "GET", "/hubs/elevator" },
    };

    [Theory]
    [MemberData(nameof(Responses))]
    public async Task Response_CarriesTheSecurityHeaders(string method, string route)
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(route, UriKind.Relative));
        if (method == "POST")
        {
            request.Content = new StringContent("""{"floor":2}""", System.Text.Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, Http.Cancellation);

        ShouldHaveSecurityHeaders(response);
    }

    [Fact]
    public async Task RateLimitRefusal_CarriesTheSecurityHeaders()
    {
        await using var factory = new ApiFactory()
            .WithSetting("RateLimiting:Read:TokenLimit", "1")
            .WithSetting("RateLimiting:Read:TokensPerPeriod", "1")
            .WithSetting("RateLimiting:Read:ReplenishmentPeriod", "01:00:00");
        using var client = factory.CreateClient();
        using var first = await client.GetRouteAsync(ApiRoutes.Elevator);

        using var refused = await client.GetRouteAsync(ApiRoutes.Elevator);

        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        ShouldHaveSecurityHeaders(refused);
    }

    [Fact]
    public async Task Response_InProduction_CarriesTheSecurityHeadersAndHsts()
    {
        await using var factory = new ApiFactory().InEnvironment(Environments.Production);
        // HSTS is never sent to localhost, so the request names another host.
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://elevator.test") });

        using var response = await client.GetRouteAsync(ApiRoutes.Elevator);

        ShouldHaveSecurityHeaders(response);
        response.Headers.GetValues("Strict-Transport-Security").Single().ShouldContain("max-age=31536000");
    }

    [Fact]
    public async Task Response_DoesNotNameTheServer()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetRouteAsync(ApiRoutes.Elevator);

        response.Headers.Server.ShouldBeEmpty();
    }

    private static void ShouldHaveSecurityHeaders(HttpResponseMessage response)
    {
        Header(response, "Content-Security-Policy").ShouldBe(ExpectedPolicy);
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
        Header(response, "Referrer-Policy").ShouldBe("no-referrer");
        Header(response, "Cross-Origin-Opener-Policy").ShouldBe("same-origin");
        var permissions = Header(response, "Permissions-Policy");
        foreach (var feature in new[] { "camera=()", "microphone=()", "geolocation=()", "payment=()", "usb=()" })
        {
            permissions.ShouldContain(feature);
        }
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? values.Single()
            : throw new ShouldAssertException($"{(int)response.StatusCode} response to {response.RequestMessage?.RequestUri} has no {name} header.");
}
