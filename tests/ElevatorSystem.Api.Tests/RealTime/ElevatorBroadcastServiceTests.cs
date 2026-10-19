using ElevatorSystem.Api.RealTime;
using ElevatorSystem.Application;
using ElevatorSystem.Application.Simulation;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;
using ElevatorSystem.Domain.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace ElevatorSystem.Api.Tests.RealTime;

public sealed class ElevatorBroadcastServiceTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = new(ApiFactory.StartTime);
    private readonly FakeLogger<ElevatorBroadcastService> _logger = new();
    private readonly ElevatorEventBuffer _buffer;
    private readonly ElevatorController _controller;
    private ElevatorBroadcastService? _service;

    public ElevatorBroadcastServiceTests()
    {
        _buffer = new ElevatorEventBuffer(
            Options.Create(new RealTimeOptions { EventBufferCapacity = 1024 }), _time, NullLogger<ElevatorEventBuffer>.Instance);
        _controller = new ElevatorController(
            new Elevator(Building.Default, _time),
            new FifoSchedulingStrategy(),
            new SimulatedElevatorDrive(TimeSpan.FromSeconds(1), _time),
            _buffer,
            new ElevatorControllerOptions(),
            _time,
            NullLogger<ElevatorController>.Instance);
    }

    [Fact]
    public async Task Broadcast_SendsEveryEventAsActivity_InOrder()
    {
        var hub = new FakeHubContext();
        PublishDoorEvents(1, 2, 3);

        await StartAsync(hub);

        await Eventually.HoldsAsync(() => hub.Activities.Count == 3, "three activities", cancellationToken: TestContext.Current.CancellationToken);
        hub.Activities.Select(activity => (activity.Kind, activity.Floor))
            .ShouldBe([(ElevatorActivityKind.DoorsOpened, 1), (ElevatorActivityKind.DoorsOpened, 2), (ElevatorActivityKind.DoorsOpened, 3)]);
    }

    [Fact]
    public async Task Broadcast_ABurstOfEvents_SendsOneStatusNotOnePerEvent()
    {
        var hub = new FakeHubContext();
        PublishDoorEvents([.. Enumerable.Range(0, 200).Select(i => (i % 10) + 1)]);

        await StartAsync(hub);

        await Eventually.HoldsAsync(() => hub.Activities.Count == 200, "every activity of the burst", cancellationToken: TestContext.Current.CancellationToken);
        hub.Statuses.Count.ShouldBe(1, "the elevator's version did not change, so repeating its status is noise");
        hub.Statuses[0].Version.ShouldBe(_controller.Elevator.Snapshot().Version);
    }

    [Fact]
    public async Task Broadcast_AfterTheElevatorChanges_SendsTheNewStatus()
    {
        var hub = new FakeHubContext();
        await StartAsync(hub);
        PublishDoorEvents(1);
        await Eventually.HoldsAsync(() => hub.Statuses.Count == 1, "the first status", cancellationToken: TestContext.Current.CancellationToken);

        _controller.EmergencyStop();

        await Eventually.HoldsAsync(() => hub.Statuses.Count == 2, "a status for the new version", cancellationToken: TestContext.Current.CancellationToken);
        hub.Statuses[1].Status.ShouldBe(ElevatorStatus.OutOfService);
        hub.Statuses[1].Version.ShouldBeGreaterThan(hub.Statuses[0].Version);
        hub.Activities[^1].Kind.ShouldBe(ElevatorActivityKind.OutOfService);
        hub.Attempts[^1].ShouldBeOfType<ElevatorStatusDto>("the status follows the activity that changed it");
    }

    [Fact]
    public async Task Broadcast_WhenSendingFails_LogsAndCarriesOnWithTheNextMessages()
    {
        var hub = FakeHubContext.Throwing();
        PublishDoorEvents(1, 2, 3);

        await StartAsync(hub);

        await Eventually.HoldsAsync(
            () => _logger.Collector.GetSnapshot().Count(record => record.Id.Id == 3001) == 4,
            "a logged failure for each of three activities and a status",
            cancellationToken: TestContext.Current.CancellationToken);
        hub.Attempts.Count.ShouldBe(4);
        hub.Statuses.Count.ShouldBe(1);
    }

    public async ValueTask DisposeAsync()
    {
        if (_service is not null)
        {
            await _service.StopAsync(CancellationToken.None);
            _service.Dispose();
        }
    }

    private async Task StartAsync(FakeHubContext hub)
    {
        _service = new ElevatorBroadcastService(_buffer, _controller, hub, _logger);
        await _service.StartAsync(TestContext.Current.CancellationToken);
    }

    private void PublishDoorEvents(params int[] floors)
    {
        foreach (var floor in floors)
        {
            _buffer.Publish(new DoorsOpened(ApiFactory.StartTime, new Floor(floor)));
        }
    }
}
