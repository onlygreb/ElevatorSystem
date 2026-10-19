using System.Text.Json;
using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace ElevatorSystem.Web.Tests.Services;

public sealed class ElevatorHubClientTests
{
    [Fact]
    public void ConfigureJson_OnSignalRDefaults_WritesEnumsAsNames()
    {
        var options = HubPayloadOptions();
        var status = TestData.Status(floor: 4, status: ElevatorStatus.MovingUp, travelDirection: CallDirection.Up);

        var json = JsonSerializer.Serialize(status, options);

        json.ShouldContain("\"status\":\"MovingUp\"");
        json.ShouldContain("\"travelDirection\":\"Up\"");
    }

    [Fact]
    public void ConfigureJson_OnSignalRDefaults_ReadsWhatTheServerSends()
    {
        var options = HubPayloadOptions();
        const string ActivityJson = """{"occurredAt":"2026-10-19T12:00:00+00:00","kind":"RequestRejected","floor":12,"rejectionReason":"InvalidFloor"}""";

        var activity = JsonSerializer.Deserialize<ElevatorActivityDto>(ActivityJson, options);

        activity.ShouldNotBeNull();
        activity.Kind.ShouldBe(ElevatorActivityKind.RequestRejected);
        activity.RejectionReason.ShouldBe(RejectionReason.InvalidFloor);
    }

    [Fact]
    public void ConfigureJson_PutsTheContractsContextFirst()
    {
        var options = HubPayloadOptions();

        options.TypeInfoResolverChain[0].ShouldBe(ContractsJsonContext.Default);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 5)]
    [InlineData(5, 30)]
    [InlineData(1_000, 30)]
    public void Delay_GrowsAndThenStaysAtThirtySeconds(long previousAttempts, int expectedSeconds) =>
        ReconnectBackoff.Delay(previousAttempts).ShouldBe(TimeSpan.FromSeconds(expectedSeconds));

    [Fact]
    public void NextRetryDelay_NeverGivesUp()
    {
        var policy = new ReconnectBackoff();

        var delay = policy.NextRetryDelay(new RetryContext { PreviousRetryCount = long.MaxValue, ElapsedTime = TimeSpan.FromDays(1) });

        delay.ShouldNotBeNull("a null delay would stop reconnecting and leave a lobby screen frozen");
    }

    private static JsonSerializerOptions HubPayloadOptions()
    {
        // The options SignalR's JSON protocol starts from, before ours are applied.
        var options = new JsonHubProtocolOptions().PayloadSerializerOptions;
        ElevatorHubClient.ConfigureJson(options);
        return options;
    }
}
