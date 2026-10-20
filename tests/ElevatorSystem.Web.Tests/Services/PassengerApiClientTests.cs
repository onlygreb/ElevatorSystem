using System.Net;
using System.Text;
using System.Text.Json;
using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Services;

namespace ElevatorSystem.Web.Tests.Services;

public sealed class PassengerApiClientTests
{
    private static readonly Guid PassengerId = Guid.Parse("7d1c2a4e-0000-4000-8000-000000000001");
    private static readonly Guid RequestId = Guid.Parse("7d1c2a4e-0000-4000-8000-000000000002");

    [Fact]
    public async Task JoinAsync_PostsAndReturnsTheNewPassenger()
    {
        var handler = new StubHandler(Accepted());

        var (accepted, outcome) = await ClientFor(handler).JoinAsync(Xunit.TestContext.Current.CancellationToken);

        handler.Requests.ShouldHaveSingleItem().Method.ShouldBe(HttpMethod.Post);
        handler.Requests[0].Uri.AbsolutePath.ShouldBe(ApiRoutes.Passengers);
        accepted.ShouldBe(new PassengerAcceptedResponse(PassengerId, RequestId));
        outcome.RequestId.ShouldBe(RequestId);
    }

    [Fact]
    public async Task CallElevatorAsync_PostsTheDirectionAsContractJson()
    {
        var handler = new StubHandler(Accepted());

        var outcome = await ClientFor(handler).CallElevatorAsync(PassengerId, CallDirection.Up, Xunit.TestContext.Current.CancellationToken);

        handler.Requests[0].Uri.AbsolutePath.ShouldBe(ApiRoutes.PassengerRoute(PassengerId) + "/hall-call");
        handler.Requests[0].Body.ShouldBe("""{"direction":"Up"}""");
        outcome.IsAccepted.ShouldBeTrue();
    }

    [Fact]
    public async Task ChooseFloorAsync_PostsTheFloor()
    {
        var handler = new StubHandler(Accepted());

        await ClientFor(handler).ChooseFloorAsync(PassengerId, 7, Xunit.TestContext.Current.CancellationToken);

        handler.Requests[0].Uri.AbsolutePath.ShouldBe(ApiRoutes.PassengerRoute(PassengerId) + "/car-call");
        handler.Requests[0].Body.ShouldBe("""{"floor":7}""");
    }

    [Theory]
    [InlineData("board")]
    [InlineData("leave")]
    public async Task BoardAndLeave_PostToTheirRoutes(string step)
    {
        var handler = new StubHandler(Accepted());
        var client = ClientFor(handler);

        await (step == "board"
            ? client.BoardAsync(PassengerId, Xunit.TestContext.Current.CancellationToken)
            : client.LeaveAsync(PassengerId, Xunit.TestContext.Current.CancellationToken));

        handler.Requests[0].Uri.AbsolutePath.ShouldBe(ApiRoutes.PassengerRoute(PassengerId) + "/" + step);
    }

    [Fact]
    public async Task GetAsync_ReadsThePassenger()
    {
        var passenger = new PassengerDto(PassengerId, 4, InCar: false, Waiting: CallDirection.Down, Destination: null, Simulated: false);
        var client = ClientFor(new StubHandler(StubHandler.Json(JsonSerializer.Serialize(passenger, ContractsJsonContext.Default.PassengerDto))));

        (await client.GetAsync(PassengerId, Xunit.TestContext.Current.CancellationToken)).ShouldBe(passenger);
    }

    [Fact]
    public async Task GetAsync_OfAPassengerTheBuildingForgot_IsNull()
    {
        var client = ClientFor(new StubHandler(new HttpResponseMessage(HttpStatusCode.NotFound)));

        (await client.GetAsync(PassengerId, Xunit.TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_NetworkFailure_IsNull()
    {
        var client = ClientFor(new StubHandler(new HttpRequestException("connection refused")));

        (await client.GetAsync(PassengerId, Xunit.TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "Too many presses in a short time. Please wait a moment and try again.")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "The elevator service can't be reached. Please try again.")]
    [InlineData(HttpStatusCode.Conflict, "The elevator is out of service right now. Please use the stairs or try again later.")]
    [InlineData(HttpStatusCode.BadRequest, "Your call couldn't be completed. Please try again.")]
    public async Task Refusal_BecomesALocalisedMessage(HttpStatusCode status, string expected)
    {
        using var culture = TestLocalisation.Culture("en-US");
        var client = ClientFor(new StubHandler(new HttpResponseMessage(status)));

        var outcome = await client.BoardAsync(PassengerId, Xunit.TestContext.Current.CancellationToken);

        outcome.IsAccepted.ShouldBeFalse();
        outcome.Message.ShouldBe(expected);
    }

    [Fact]
    public async Task HtmlErrorPage_DoesNotLeakItsText()
    {
        using var culture = TestLocalisation.Culture("en-US");
        var response = new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>upstream db-01 down</html>", Encoding.UTF8, "text/html"),
        };

        var outcome = await ClientFor(new StubHandler(response)).LeaveAsync(PassengerId, Xunit.TestContext.Current.CancellationToken);

        outcome.Message.ShouldBe("The elevator service can't be reached. Please try again.");
    }

    [Fact]
    public async Task NetworkFailure_ReportsTheServiceAsUnreachable()
    {
        using var culture = TestLocalisation.Culture("en-US");
        var client = ClientFor(new StubHandler(new HttpRequestException("connection refused")));

        var (accepted, outcome) = await client.JoinAsync(Xunit.TestContext.Current.CancellationToken);

        accepted.ShouldBeNull();
        outcome.Message.ShouldBe("The elevator service can't be reached. Please try again.");
    }

    [Fact]
    public async Task CallerCancels_Throws()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var client = ClientFor(new StubHandler(Accepted()));

        await Should.ThrowAsync<OperationCanceledException>(() => client.BoardAsync(PassengerId, cancellation.Token));
    }

    private static PassengerApiClient ClientFor(StubHandler handler) => new(handler.Client(), TestLocalisation.Localizer());

    private static HttpResponseMessage Accepted() =>
        StubHandler.Json(
            JsonSerializer.Serialize(new PassengerAcceptedResponse(PassengerId, RequestId), ContractsJsonContext.Default.PassengerAcceptedResponse),
            HttpStatusCode.Accepted);
}
