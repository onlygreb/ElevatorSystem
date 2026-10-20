using System.Net;
using ElevatorSystem.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ElevatorSystem.Api.Tests.Endpoints;

/// <summary>Errors under /api are problem details that reveal nothing internal; pages keep their HTML.</summary>
public sealed class ApiErrorTests
{
    [Theory]
    [InlineData("/api/v1/nothing-here")]
    [InlineData("/api/v2/elevator")]
    [InlineData(ApiRoutes.Base)]
    public async Task UnknownApiRoute_Returns404Problem(string route)
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetRouteAsync(route);

        await response.ShouldBeProblemAsync(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("PUT", ApiRoutes.HallCalls)]
    [InlineData("DELETE", ApiRoutes.Elevator)]
    public async Task WrongMethod_Returns405Problem(string method, string route)
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(route, UriKind.Relative));

        using var response = await client.SendAsync(request, Http.Cancellation);

        await response.ShouldBeProblemAsync(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task GetOnAPostRoute_ReturnsAProblemNotThePage()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetRouteAsync(ApiRoutes.HallCalls);

        // Routing answers 404 rather than 405 for GET here, because the UI's static-file fallback route
        // is a GET candidate for every path; what matters is that the answer is a problem, not HTML.
        await response.ShouldBeProblemAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UnknownPage_KeepsTheHtmlNotFoundPage()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetRouteAsync("/no-such-page");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("text/html");
    }

    [Fact]
    public async Task UnhandledException_InProduction_Returns500ProblemWithoutInternals()
    {
        var clock = new FailingClock();
        await using var factory = new ApiFactory()
            .InEnvironment(Environments.Production)
            .WithoutEngine()
            .WithServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));
        using var client = factory.CreateClient();
        clock.Fail = true;

        using var response = await client.PostJsonAsync(ApiRoutes.CarCalls, """{"floor":3}""");

        var problem = await response.ShouldBeProblemAsync(HttpStatusCode.InternalServerError);
        var body = problem.GetRawText();
        body.ShouldNotContain(FailingClock.Secret);
        body.ShouldNotContain(nameof(InvalidOperationException));
        body.ShouldNotContain(nameof(FailingClock));
    }

    [Theory]
    [InlineData("/openapi/v1.json")]
    [InlineData("/scalar")]
    public async Task ApiDescription_InProduction_IsNotExposed(string route)
    {
        await using var factory = new ApiFactory().InEnvironment(Environments.Production);
        using var client = factory.CreateClient();

        using var response = await client.GetRouteAsync(route);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>A clock that breaks on demand, to make an endpoint fail in a way no input can.</summary>
    private sealed class FailingClock : TimeProvider
    {
        public const string Secret = "secret-internal-detail";

        private volatile bool _fail;

        public bool Fail
        {
            get => _fail;
            set => _fail = value;
        }

        public override DateTimeOffset GetUtcNow() => _fail ? throw new InvalidOperationException(Secret) : base.GetUtcNow();
    }
}
