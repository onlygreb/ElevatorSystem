using System.Text.Json;
using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Services;

namespace ElevatorSystem.Web.Tests.Services;

public sealed class ElevatorApiClientTests
{
    [Fact]
    public async Task GetConfigurationAsync_ReadsTheContract()
    {
        var json = JsonSerializer.Serialize(TestData.Configuration(travelSecondsPerFloor: 2), ContractsJsonContext.Default.ElevatorConfigurationDto);
        var handler = new StubHandler(StubHandler.Json(json));
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
             "targetFloors":[7],"version":42,"takenAt":"2026-10-19T12:00:01+00:00","doorsCloseAt":"2026-10-19T12:00:04+00:00"}
            """;
        var client = ClientFor(new StubHandler(StubHandler.Json(StatusJson)));

        var status = await client.GetStatusAsync(Xunit.TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ElevatorStatus.MovingUp);
        status.PendingCalls.ShouldHaveSingleItem().Call.Direction.ShouldBe(CallDirection.Down);
        status.Version.ShouldBe(42);
        status.DoorsCloseAt.ShouldBe(TestData.Noon.AddSeconds(4));
    }

    [Theory]
    [InlineData(RejectionReason.InvalidFloor, "That floor isn't served by this elevator.")]
    [InlineData(RejectionReason.InvalidDirection, "That direction isn't available from this floor.")]
    [InlineData(RejectionReason.OutOfService, "The elevator is out of service right now. Please use the stairs or try again later.")]
    [InlineData(RejectionReason.Busy, "The elevator is handling many calls. Please try again in a moment.")]
    public void EveryRejectionReason_HasAPassengerFacingMessage(RejectionReason reason, string expected)
    {
        using var culture = TestLocalisation.Culture("en-US");

        TestLocalisation.Localizer()[ElevatorApiClient.MessageKeyFor(reason)].Value.ShouldBe(expected);
    }

    private static ElevatorApiClient ClientFor(StubHandler handler) => new(handler.Client());
}
