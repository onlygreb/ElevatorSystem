using System.Net;
using System.Text.Json;
using ElevatorSystem.Contracts;
using static ElevatorSystem.Integration.Tests.CarStates;

namespace ElevatorSystem.Integration.Tests;

/// <summary>Without the broker nothing is reported as accepted, nothing crashes, and everything recovers by itself.</summary>
[Collection(BrokerOutageGroup.Name)]
public sealed class BrokerOutageTests(RestartableSystemFixture system)
{
    [Fact]
    public async Task BrokerOutage_TheApiAnswers503WithoutCrashing_AndRecoversAfterwards()
    {
        var environment = await system.CreateEnvironmentAsync();
        await using var engine = EngineProcess.Start("engine-1", environment);
        await using var api = new ApiHost(environment);
        await engine.WaitForRoleAsync("leader");
        var known = await api.WaitForStatusAsync(Resting, "the car to rest with nothing to do");

        await system.Broker.StopAsync(TestContext.Current.CancellationToken);
        try
        {
            using var refused = await Eventually.ReturnsAsync(
                async () => await api.CallAsync(ApiRoutes.CarCalls, """{"floor":7}""") is { StatusCode: HttpStatusCode.ServiceUnavailable } response ? response : null,
                "the API to refuse calls it cannot pass on");
            refused.Headers.RetryAfter.ShouldNotBeNull();
            using var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            problem.RootElement.GetProperty(ApiRoutes.RejectionReasonExtension).GetString().ShouldBe(nameof(RejectionReason.Busy));
            (await api.StatusAsync()).ShouldNotBeNull().Version.ShouldBeGreaterThanOrEqualTo(known.Version, "reads still answer with the last known state");
            engine.HasExited.ShouldBeFalse("a broker outage must not crash the engine");
        }
        finally
        {
            await system.Broker.StartAsync(TestContext.Current.CancellationToken);
        }

        var accepted = await Eventually.ReturnsAsync(
            async () => await api.CallAsync(ApiRoutes.CarCalls, """{"floor":7}""") is { StatusCode: HttpStatusCode.Accepted } response ? response : null,
            "the API to accept calls again",
            TimeSpan.FromSeconds(60));
        accepted.Dispose();
        await api.WaitForStatusAsync(status => status is { CurrentFloor: 7, Status: ElevatorStatus.DoorOpen or ElevatorStatus.Idle }, "the car to serve the call made after the outage", TimeSpan.FromSeconds(60));
    }
}
