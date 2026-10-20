using System.Net;
using System.Text;
using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Services;

namespace ElevatorSystem.Web.Tests.Services;

/// <summary>The staff HTTP client: antiforgery tokens, idempotency keys and how refusals are reported.</summary>
public sealed class StaffApiClientTests
{
    private const string Session = """{"email":"admin@elevator.test","roles":["Admin"],"twoFactorEnabled":true,"twoFactorVerified":true}""";

    [Fact]
    public async Task Post_CarriesTheAntiforgeryToken_FetchedOnce()
    {
        var handler = new ScriptedHandler(Token("t1"), Json(HttpStatusCode.Accepted, """{"commandId":"0192b6a4-0000-7000-8000-000000000001","action":"EmergencyStop"}"""),
            Json(HttpStatusCode.Accepted, """{"commandId":"0192b6a4-0000-7000-8000-000000000002","action":"StartMaintenance"}"""));
        using var client = Client(handler);

        await client.RequestServiceActionAsync(ServiceAction.EmergencyStop, Guid.NewGuid(), Cancellation);
        await client.RequestServiceActionAsync(ServiceAction.StartMaintenance, Guid.NewGuid(), Cancellation);

        handler.Requests.Select(request => request.Path).ShouldBe([ApiRoutes.Antiforgery, ApiRoutes.ServiceActions, ApiRoutes.ServiceActions]);
        handler.Requests[1].Headers["X-CSRF-TOKEN"].ShouldBe("t1");
        handler.Requests[2].Headers["X-CSRF-TOKEN"].ShouldBe("t1");
    }

    [Fact]
    public async Task ServiceAction_SendsTheIdempotencyKey()
    {
        var key = Guid.NewGuid();
        var handler = new ScriptedHandler(Token("t1"), Json(HttpStatusCode.Accepted, """{"commandId":"0192b6a4-0000-7000-8000-000000000001","action":"ReturnToService"}"""));
        using var client = Client(handler);

        var result = await client.RequestServiceActionAsync(ServiceAction.ReturnToService, key, Cancellation);

        result.Value.ShouldNotBeNull().Action.ShouldBe(ServiceAction.ReturnToService);
        handler.Requests[1].Headers[ApiRoutes.IdempotencyKeyHeader].ShouldBe(key.ToString());
        handler.Requests[1].Body.ShouldBe("""{"action":"ReturnToService"}""");
    }

    [Fact]
    public async Task SignIn_FetchesANewTokenForTheNextPost_BecauseTokensBelongToTheUser()
    {
        var handler = new ScriptedHandler(Token("anonymous"), Json(HttpStatusCode.OK, Session), Token("signed-in"), Json(HttpStatusCode.NoContent, ""));
        using var client = Client(handler);

        await client.SignInAsync(new SignInRequest("admin@elevator.test", "pw"), Cancellation);
        await client.SignOutAsync(Cancellation);

        handler.Requests.Select(request => request.Path).ShouldBe([ApiRoutes.Antiforgery, ApiRoutes.SignIn, ApiRoutes.Antiforgery, ApiRoutes.SignOut]);
        handler.Requests[3].Headers["X-CSRF-TOKEN"].ShouldBe("signed-in");
    }

    [Fact]
    public async Task ARefusedToken_IsFetchedAgain_AndTheRequestSentOnceMore()
    {
        var handler = new ScriptedHandler(
            Token("stale"), Problem(HttpStatusCode.BadRequest, "Antiforgery"), Token("fresh"), Json(HttpStatusCode.OK, """{"recoveryCodes":["AAAAA-11111"]}"""));
        using var client = Client(handler);

        var result = await client.EnableTwoFactorAsync("123456", Cancellation);

        result.Succeeded.ShouldBeTrue();
        handler.Requests[3].Headers["X-CSRF-TOKEN"].ShouldBe("fresh");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "InvalidCredentials", StaffFailure.InvalidCredentials)]
    [InlineData(HttpStatusCode.Unauthorized, "TwoFactorRequired", StaffFailure.TwoFactorRequired)]
    [InlineData(HttpStatusCode.Unauthorized, "InvalidTwoFactorCode", StaffFailure.InvalidTwoFactorCode)]
    [InlineData(HttpStatusCode.BadRequest, "InvalidTwoFactorCode", StaffFailure.InvalidTwoFactorCode)]
    [InlineData(HttpStatusCode.Unauthorized, null, StaffFailure.SessionExpired)]
    [InlineData(HttpStatusCode.Forbidden, null, StaffFailure.NotAllowed)]
    [InlineData(HttpStatusCode.TooManyRequests, null, StaffFailure.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Busy", StaffFailure.Unavailable)]
    [InlineData(HttpStatusCode.Conflict, null, StaffFailure.Unexpected)]
    public void Refusals_MapToWhatTheAdminViewExplains(HttpStatusCode status, string? reason, StaffFailure expected)
    {
        StaffApiClient.FailureFor(status, reason).ShouldBe(expected);
    }

    [Fact]
    public async Task AnUnreachableServer_IsReportedAsUnavailable()
    {
        using var client = Client(new ScriptedHandler(new HttpRequestException("refused")));

        var result = await client.GetSessionAsync(Cancellation);

        result.Failure.ShouldBe(StaffFailure.Unavailable);
    }

    [Fact]
    public async Task AuditPage_AsksForEntriesBeforeTheGivenSequence()
    {
        var handler = new ScriptedHandler(Json(HttpStatusCode.OK, """{"entries":[],"nextBefore":null}"""));
        using var client = Client(handler);

        await client.GetAuditPageAsync(42, Cancellation);

        handler.Requests[0].Path.ShouldBe(ApiRoutes.AuditEntries);
        handler.Requests[0].Query.ShouldBe("?before=42");
    }

    private static CancellationToken Cancellation => Xunit.TestContext.Current.CancellationToken;

    private static StaffApiClient Client(ScriptedHandler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });

    private static Func<HttpResponseMessage> Token(string token) =>
        Json(HttpStatusCode.OK, $$"""{"headerName":"X-CSRF-TOKEN","requestToken":"{{token}}"}""");

    private static Func<HttpResponseMessage> Problem(HttpStatusCode status, string reason) =>
        () => new HttpResponseMessage(status) { Content = new StringContent($$"""{"status":{{(int)status}},"reason":"{{reason}}"}""", Encoding.UTF8, "application/problem+json") };

    private static Func<HttpResponseMessage> Json(HttpStatusCode status, string body) =>
        () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record RecordedRequest(HttpMethod Method, string Path, string Query, Dictionary<string, string> Headers, string? Body);

    /// <summary>Answers each request with the next scripted response, in order, and records what was sent.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses;
        private readonly Exception? _failure;

        public ScriptedHandler(params Func<HttpResponseMessage>[] responses) => _responses = new Queue<Func<HttpResponseMessage>>(responses);

        public ScriptedHandler(Exception failure)
        {
            _responses = new Queue<Func<HttpResponseMessage>>();
            _failure = failure;
        }

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_failure is not null)
            {
                throw _failure;
            }

            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.AbsolutePath,
                request.RequestUri.Query,
                request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value)),
                body));
            return _responses.Dequeue()();
        }
    }
}
