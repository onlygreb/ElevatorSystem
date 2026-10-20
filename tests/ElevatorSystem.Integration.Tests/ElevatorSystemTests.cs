using ElevatorSystem.Application.Ports;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using ElevatorSystem.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static ElevatorSystem.Integration.Tests.CarStates;

namespace ElevatorSystem.Integration.Tests;

/// <summary>
/// The whole system through its real broker and database: calls in through the API, the engine driving the car,
/// updates out to hub clients, and the fail-safe behaviour of ADR-0007 when processes die (ADR-0021).
/// </summary>
[Collection(SystemGroup.Name)]
public sealed class ElevatorSystemTests(SystemFixture system)
{
    private static readonly Dictionary<string, string> SlowTravel = new() { ["Elevator:TravelTimePerFloor"] = Timings.SlowTravelTimePerFloor };

    [Fact]
    public async Task HallCall_ThroughTheApi_MovesTheCar_AndStatusUpdatesReachHubClients()
    {
        var environment = await system.CreateEnvironmentAsync();
        await using var engine = EngineProcess.Start("engine-1", environment);
        await using var api = new ApiHost(environment);
        await using var hub = await api.ConnectHubAsync();
        await engine.WaitForRoleAsync("leader");

        var requestId = await api.AcceptedCallAsync(ApiRoutes.HallCalls, """{"floor":5,"direction":"Down"}""");

        await hub.WaitForActivityAsync(activity => activity.Kind == ElevatorActivityKind.CallQueued && activity.RequestId == requestId, "the call to be queued");
        await hub.WaitForActivityAsync(activity => activity.Kind == ElevatorActivityKind.CallServed && activity.RequestId == requestId, "the call to be served");
        await hub.WaitForStatusAsync(status => status is { CurrentFloor: 5, Status: ElevatorStatus.DoorOpen }, "the hub to show the car at floor 5 with its door open");
        hub.Statuses.Select(status => status.Version).ShouldBeInOrder(SortDirection.Ascending, "hub clients see the car move forwards only");
        engine.Output.ShouldNotContain(line => (line.Contains("fail:", StringComparison.Ordinal) || line.Contains("crit:", StringComparison.Ordinal)) && line.Contains(" ElevatorSystem.", StringComparison.Ordinal),
            "a fresh start on an empty database logs no errors: migrations run before anything uses the schema");
    }

    [Fact]
    public async Task DuplicatedCommand_IsAppliedOnce()
    {
        var environment = await system.CreateEnvironmentAsync();
        await using var engine = EngineProcess.Start("engine-1", environment);
        await using var api = new ApiHost(environment);
        await using var hub = await api.ConnectHubAsync();
        await engine.WaitForRoleAsync("leader");
        await using var connection = BrokerConnection(environment);
        var publisher = new RabbitMqElevatorCommandPublisher(connection);
        var command = new CarCallCommandV1(Guid.CreateVersion7(), DateTimeOffset.UtcNow, 6);

        await publisher.PublishAsync(command, TestContext.Current.CancellationToken);
        await publisher.PublishAsync(command, TestContext.Current.CancellationToken);
        var later = await api.AcceptedCallAsync(ApiRoutes.CarCalls, """{"floor":2}""");

        await hub.WaitForActivityAsync(activity => activity.Kind == ElevatorActivityKind.CallServed && activity.RequestId == later, "the call sent after both copies to be served");
        hub.Activities.Where(activity => activity.RequestId == command.CommandId).Select(activity => activity.Kind)
            .ShouldBe([ElevatorActivityKind.CallQueued, ElevatorActivityKind.CallServed], "the second copy is dropped, not merged or served again");
    }

    [Fact]
    public async Task KillingTheLeader_LetsTheStandbyTakeOverFromTheLastSnapshot_WithoutLosingAnAcceptedCall()
    {
        var environment = await system.CreateEnvironmentAsync();
        await using var first = EngineProcess.Start("engine-1", environment);
        await first.WaitForRoleAsync("leader");
        await using var second = EngineProcess.Start("engine-2", environment);
        await second.WaitForRoleAsync("standby");
        await using var api = new ApiHost(environment);
        await using var hub = await api.ConnectHubAsync();
        var toNine = await api.AcceptedCallAsync(ApiRoutes.CarCalls, """{"floor":9}""");
        var beforeKill = await api.WaitForStatusAsync(
            status => status.Status == ElevatorStatus.MovingUp && status.PendingCalls.Any(pending => pending.Call.Floor == 9),
            "the car to be on its way to floor 9");

        var activitiesBeforeKill = hub.Activities.Count;
        first.Kill();
        var whileNobodyLeads = await api.AcceptedCallAsync(ApiRoutes.HallCalls, """{"floor":3,"direction":"Up"}""");

        await second.WaitForRoleAsync("leader");
        // Statuses arriving together are coalesced (ADR-0015), so the take-over shows in the activity feed.
        var takeOver = await hub.WaitForActivityAsync(activity => activity.Kind == ElevatorActivityKind.DoorsOpened, "the new leader's start-up door cycle", fromIndex: activitiesBeforeKill);
        takeOver.Floor.ShouldBeGreaterThanOrEqualTo(beforeKill.CurrentFloor, "the new leader restores the car where the old one left it");
        await hub.WaitForActivityAsync(activity => activity.Kind == ElevatorActivityKind.CallServed && activity.RequestId == toNine, "the call accepted before the crash to be served");
        await hub.WaitForActivityAsync(activity => activity.Kind == ElevatorActivityKind.CallServed && activity.RequestId == whileNobodyLeads, "the call accepted during the failover to be served");
        hub.Statuses.Select(status => status.Version).ShouldBeInOrder(SortDirection.Ascending, "the restored version continues above what clients saw");
    }

    [Fact]
    public async Task RestartingTheOnlyEngine_RestoresTheFloorAndThePendingCalls()
    {
        var environment = await system.CreateEnvironmentAsync();
        var engine = EngineProcess.Start("engine-1", environment, SlowTravel);
        await using var api = new ApiHost(environment);
        await engine.WaitForRoleAsync("leader");
        var toEight = await api.AcceptedCallAsync(ApiRoutes.CarCalls, """{"floor":8}""");
        var fromFive = await api.AcceptedCallAsync(ApiRoutes.HallCalls, """{"floor":5,"direction":"Down"}""");
        var seen = await api.WaitForStatusAsync(status => status.PendingCalls.Count == 2 && status.Status == ElevatorStatus.MovingUp, "both calls to be pending");

        engine.Kill();
        await engine.DisposeAsync();
        var stored = (await StoredSnapshotAsync(environment)).ShouldNotBeNull();
        await using var hub = await api.ConnectHubAsync();
        await using var restarted = EngineProcess.Start("engine-1b", environment, SlowTravel);

        stored.Version.ShouldBeGreaterThanOrEqualTo(seen.Version, "a status clients saw was saved before it was published");
        stored.PendingCalls.Select(pending => pending.Call.Floor.Number).ShouldContain(8, "the car call was still on its way when the engine died");
        // Statuses arriving together are coalesced (ADR-0015), so the restored floor shows in the activity feed.
        var doorCycle = await hub.WaitForActivityAsync(activity => activity.Kind == ElevatorActivityKind.DoorsOpened, "the restarted engine's start-up door cycle");
        doorCycle.Floor.ShouldBe(stored.CurrentFloor.Number);
        var restored = await hub.WaitForStatusAsync(status => status.Version > stored.Version, "the restarted engine's first status");
        // The start-up door cycle serves a call at the floor where the car stands.
        var stillPending = stored.PendingCalls.Select(pending => pending.Call.Floor.Number).Where(floor => floor != stored.CurrentFloor.Number);
        restored.PendingCalls.Select(pending => pending.Call.Floor).ShouldBe(stillPending, "the other restored calls are still pending in FIFO order");
        await hub.WaitForActivityAsync(activity => activity.Kind == ElevatorActivityKind.CallServed && activity.RequestId == toEight, "the restored car call to be served", TimeSpan.FromSeconds(45));
        await hub.WaitForActivityAsync(activity => activity.Kind == ElevatorActivityKind.CallServed && activity.RequestId == fromFive, "the restored hall call to be served", TimeSpan.FromSeconds(45));
    }

    [Fact]
    public async Task WhileTheEngineIsDown_TheApiKeepsServingTheLastKnownState_AndQueuesCalls()
    {
        var environment = await system.CreateEnvironmentAsync();
        var engine = EngineProcess.Start("engine-1", environment);
        await using var api = new ApiHost(environment);
        await engine.WaitForRoleAsync("leader");
        await api.AcceptedCallAsync(ApiRoutes.CarCalls, """{"floor":4}""");
        var last = await api.WaitForStatusAsync(status => status.CurrentFloor == 4 && Resting(status), "the car to rest at floor 4");

        engine.Kill();
        await engine.DisposeAsync();

        for (var i = 0; i < 5; i++)
        {
            (await api.StatusAsync()).ShouldNotBeNull().Version.ShouldBe(last.Version, "the API answers from the last update it received");
        }

        var queued = await api.AcceptedCallAsync(ApiRoutes.CarCalls, """{"floor":6}""");
        await using var hub = await api.ConnectHubAsync();
        (await hub.WaitForStatusAsync(_ => true, "the status on connect")).Version.ShouldBe(last.Version);
        await using var replacement = EngineProcess.Start("engine-2", environment);
        await hub.WaitForActivityAsync(activity => activity.Kind == ElevatorActivityKind.CallServed && activity.RequestId == queued, "the call accepted while no engine ran to be served");
    }

    private static RabbitMqConnection BrokerConnection(TestEnvironment environment) => new(
        Options.Create(new MessagingOptions { Uri = environment.BrokerUri, ClientName = "test", RecoveryInterval = TimeSpan.FromSeconds(1) }),
        NullLogger<RabbitMqConnection>.Instance,
        TimeProvider.System);

    private static async Task<ElevatorSnapshot?> StoredSnapshotAsync(TestEnvironment environment)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:elevatordb"] = environment.DatabaseConnectionString })
            .Build();
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddElevatorPersistence()
            .BuildServiceProvider();
        return await services.GetRequiredService<IElevatorStateStore>().LoadAsync(TestContext.Current.CancellationToken);
    }
}
