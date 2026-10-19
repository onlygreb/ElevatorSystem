using System.Net;
using System.Text;
using System.Text.Json;
using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Services;

namespace ElevatorSystem.Web.Tests.Services;

public sealed class ElevatorApiClientTests
{
    [Fact]
    public async Task RequestElevatorAsync_PostsTheHallCallAsContractJson()
    {
        var handler = new StubHandler(Accepted(Guid.NewGuid()));
        var client = ClientFor(handler);

        await client.RequestElevatorAsync(3, CallDirection.Up, Xunit.TestContext.Current.CancellationToken);

        handler.Requests.ShouldHaveSingleItem();
        handler.Requests[0].Method.ShouldBe(HttpMethod.Post);
        handler.Requests[0].Uri.AbsolutePath.ShouldBe(ApiRoutes.HallCalls);
        handler.Requests[0].Body.ShouldBe("""{"floor":3,"direction":"Up"}""");
    }

    [Fact]
    public async Task RequestDestinationAsync_PostsTheCarCall()
    {
        var handler = new StubHandler(Accepted(Guid.NewGuid()));
        var client = ClientFor(handler);

        await client.RequestDestinationAsync(7, Xunit.TestContext.Current.CancellationToken);

        handler.Requests[0].Uri.AbsolutePath.ShouldBe(ApiRoutes.CarCalls);
        handler.Requests[0].Body.ShouldBe("""{"floor":7}""");
    }

    [Fact]
    public async Task RequestElevatorAsync_Accepted_ReturnsTheRequestId()
    {
        var requestId = Guid.NewGuid();
        var client = ClientFor(new StubHandler(Accepted(requestId)));

        var outcome = await client.RequestElevatorAsync(3, CallDirection.Up, Xunit.TestContext.Current.CancellationToken);

        outcome.IsAccepted.ShouldBeTrue();
        outcome.RequestId.ShouldBe(requestId);
        outcome.Message.ShouldBeNull();
    }

    [Theory]
    [InlineData(RejectionReason.InvalidFloor, "That floor isn't served by this elevator.")]
    [InlineData(RejectionReason.InvalidDirection, "That direction isn't available from this floor.")]
    [InlineData(RejectionReason.OutOfService, "The elevator is out of service right now. Please use the stairs or try again later.")]
    [InlineData(RejectionReason.Busy, "The elevator is handling many calls. Please try again in a moment.")]
    public async Task RequestElevatorAsync_ProblemWithReason_MapsToTheLocalisedMessage(RejectionReason reason, string expected)
    {
        using var culture = TestLocalisation.Culture("en-US");
        var client = ClientFor(new StubHandler(Problem(HttpStatusCode.Conflict, reason.ToString())));

        var outcome = await client.RequestElevatorAsync(3, CallDirection.Up, Xunit.TestContext.Current.CancellationToken);

        outcome.IsAccepted.ShouldBeFalse();
        outcome.Reason.ShouldBe(reason);
        outcome.Message.ShouldBe(expected);
    }

    [Theory]
    [InlineData(RejectionReason.InvalidFloor, "Este elevador não atende esse andar.")]
    [InlineData(RejectionReason.InvalidDirection, "Essa direção não está disponível neste andar.")]
    [InlineData(RejectionReason.OutOfService, "O elevador está fora de serviço no momento. Use a escada ou tente novamente mais tarde.")]
    [InlineData(RejectionReason.Busy, "O elevador está atendendo muitas chamadas. Tente novamente em instantes.")]
    public async Task RequestDestinationAsync_ProblemWithReason_InPortuguese_MapsToThePortugueseMessage(RejectionReason reason, string expected)
    {
        using var culture = TestLocalisation.Culture("pt-BR");
        var client = ClientFor(new StubHandler(Problem(HttpStatusCode.BadRequest, reason.ToString())));

        var outcome = await client.RequestDestinationAsync(3, Xunit.TestContext.Current.CancellationToken);

        outcome.Message.ShouldBe(expected);
    }

    [Theory]
    [InlineData("1", "a number is not a reason name")]
    [InlineData("outofservice", "names are case-sensitive")]
    [InlineData("<script>", "unknown text is never shown")]
    public async Task RequestElevatorAsync_UnrecognisedReason_FallsBackToTheGenericMessage(string reason, string because)
    {
        using var culture = TestLocalisation.Culture("en-US");
        var client = ClientFor(new StubHandler(Problem(HttpStatusCode.BadRequest, reason)));

        var outcome = await client.RequestElevatorAsync(3, CallDirection.Up, Xunit.TestContext.Current.CancellationToken);

        outcome.Reason.ShouldBeNull(because);
        outcome.Message.ShouldBe("Your call couldn't be completed. Please try again.");
    }

    [Fact]
    public async Task RequestElevatorAsync_RateLimited_AsksThePassengerToWait()
    {
        using var culture = TestLocalisation.Culture("en-US");
        var client = ClientFor(new StubHandler(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));

        var outcome = await client.RequestElevatorAsync(3, CallDirection.Up, Xunit.TestContext.Current.CancellationToken);

        outcome.Message.ShouldBe("Too many presses in a short time. Please wait a moment and try again.");
    }

    [Fact]
    public async Task RequestElevatorAsync_HtmlErrorPage_DoesNotLeakItsText()
    {
        using var culture = TestLocalisation.Culture("en-US");
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("<html>upstream db-01 down</html>", Encoding.UTF8, "text/html"),
        };
        var client = ClientFor(new StubHandler(response));

        var outcome = await client.RequestElevatorAsync(3, CallDirection.Up, Xunit.TestContext.Current.CancellationToken);

        outcome.Message.ShouldBe("The elevator service can't be reached. Please try again.");
    }

    [Fact]
    public async Task RequestElevatorAsync_NetworkFailure_ReportsTheServiceAsUnreachable()
    {
        using var culture = TestLocalisation.Culture("en-US");
        var client = ClientFor(new StubHandler(new HttpRequestException("connection refused")));

        var outcome = await client.RequestElevatorAsync(3, CallDirection.Up, Xunit.TestContext.Current.CancellationToken);

        outcome.IsAccepted.ShouldBeFalse();
        outcome.Message.ShouldBe("The elevator service can't be reached. Please try again.");
    }

    [Fact]
    public async Task RequestElevatorAsync_CallerCancels_Throws()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var client = ClientFor(new StubHandler(Accepted(Guid.NewGuid())));

        await Should.ThrowAsync<OperationCanceledException>(() => client.RequestElevatorAsync(3, CallDirection.Up, cancellation.Token));
    }

    [Fact]
    public async Task GetConfigurationAsync_ReadsTheContract()
    {
        var json = JsonSerializer.Serialize(TestData.Configuration(travelSecondsPerFloor: 2), ContractsJsonContext.Default.ElevatorConfigurationDto);
        var handler = new StubHandler(Json(json));
        var client = ClientFor(handler);

        var configuration = await client.GetConfigurationAsync(Xunit.TestContext.Current.CancellationToken);

        handler.Requests[0].Uri.AbsolutePath.ShouldBe(ApiRoutes.Configuration);
        configuration.ShouldBe(TestData.Configuration(travelSecondsPerFloor: 2));
    }

    [Fact]
    public async Task GetStatusAsync_ReadsEnumsByName()
    {
        const string StatusJson = """
            {"currentFloor":4,"status":"MovingUp","travelDirection":"Up","outOfServiceCause":null,
             "pendingCalls":[{"call":{"kind":"Hall","floor":7,"direction":"Down"},"requestCount":2,"firstRequestedAt":"2026-10-19T12:00:00+00:00"}],
             "targetFloors":[7],"version":42,"takenAt":"2026-10-19T12:00:01+00:00"}
            """;
        var client = ClientFor(new StubHandler(Json(StatusJson)));

        var status = await client.GetStatusAsync(Xunit.TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ElevatorStatus.MovingUp);
        status.PendingCalls.ShouldHaveSingleItem().Call.Direction.ShouldBe(CallDirection.Down);
        status.Version.ShouldBe(42);
    }

    private static ElevatorApiClient ClientFor(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://elevator.test/") }, TestLocalisation.Localizer());

    private static HttpResponseMessage Accepted(Guid requestId) =>
        Json(JsonSerializer.Serialize(new RequestAcceptedResponse(requestId), ContractsJsonContext.Default.RequestAcceptedResponse), HttpStatusCode.Accepted);

    private static HttpResponseMessage Problem(HttpStatusCode status, string reason) => new(status)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new Dictionary<string, object> { ["title"] = "Request refused", ["status"] = (int)status, ["reason"] = reason }),
            Encoding.UTF8,
            "application/problem+json"),
    };

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body);

    /// <summary>Answers every request with one canned response (or failure) and records what was sent. No network.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage? _response;
        private readonly Exception? _failure;

        public StubHandler(HttpResponseMessage response) => _response = response;

        public StubHandler(Exception failure) => _failure = failure;

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body));
            return _failure is null ? _response! : throw _failure;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _response?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
