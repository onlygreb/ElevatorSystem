using ElevatorSystem.Api.RealTime;
using ElevatorSystem.Contracts;
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
    private ElevatorBroadcastService? _service;

    public ElevatorBroadcastServiceTests()
    {
        _buffer = new ElevatorEventBuffer(
            Options.Create(new RealTimeOptions { EventBufferCapacity = 1024 }), _time, NullLogger<ElevatorEventBuffer>.Instance);
    }

    [Fact]
    public async Task Broadcast_SendsEveryUpdateAsActivity_InOrder()
    {
        var hub = new FakeHubContext();
        PublishDoorUpdates(version: 1, 1, 2, 3);

        await StartAsync(hub);

        await Eventually.HoldsAsync(() => hub.Activities.Count == 3, "three activities", cancellationToken: TestContext.Current.CancellationToken);
        hub.Activities.Select(activity => (activity.Kind, activity.Floor))
            .ShouldBe([(ElevatorActivityKind.DoorsOpened, 1), (ElevatorActivityKind.DoorsOpened, 2), (ElevatorActivityKind.DoorsOpened, 3)]);
    }

    [Fact]
    public async Task Broadcast_ABurstOfUpdates_SendsOneStatusNotOnePerUpdate()
    {
        var hub = new FakeHubContext();
        PublishDoorUpdates(version: 7, [.. Enumerable.Range(0, 200).Select(i => (i % 10) + 1)]);

        await StartAsync(hub);

        await Eventually.HoldsAsync(() => hub.Activities.Count == 200, "every activity of the burst", cancellationToken: TestContext.Current.CancellationToken);
        hub.Statuses.Count.ShouldBe(1, "the elevator's version did not change, so repeating its status is noise");
        hub.Statuses[0].Version.ShouldBe(7);
    }

    [Fact]
    public async Task Broadcast_ABurstWithTheCarMoving_SendsAStatusForEveryFloor()
    {
        var hub = new FakeHubContext();
        for (var floor = 2; floor <= 5; floor++)
        {
            // Each move comes with a crowd of merged presses that change nothing but the queue.
            _buffer.Publish(Update(ElevatorActivityKind.Moved, floor, version: floor * 10, ElevatorStatus.MovingUp));
            for (var press = 1; press <= 5; press++)
            {
                _buffer.Publish(Update(ElevatorActivityKind.CallMerged, floor, version: (floor * 10) + press, ElevatorStatus.MovingUp));
            }
        }

        await StartAsync(hub);

        await Eventually.HoldsAsync(() => hub.Activities.Count == 24, "every activity of the burst", cancellationToken: TestContext.Current.CancellationToken);
        await Eventually.HoldsAsync(() => hub.Statuses.Count == 4, "a status per floor", cancellationToken: TestContext.Current.CancellationToken);
        hub.Statuses.Select(status => status.CurrentFloor).ShouldBe([2, 3, 4, 5], "the car must never be seen to skip a floor");
    }

    [Fact]
    public async Task Broadcast_AfterTheElevatorChanges_SendsTheNewStatus()
    {
        var hub = new FakeHubContext();
        await StartAsync(hub);
        PublishDoorUpdates(version: 1, 1);
        await Eventually.HoldsAsync(() => hub.Statuses.Count == 1, "the first status", cancellationToken: TestContext.Current.CancellationToken);

        _buffer.Publish(Update(ElevatorActivityKind.OutOfService, 1, version: 2, ElevatorStatus.OutOfService));

        await Eventually.HoldsAsync(() => hub.Statuses.Count == 2, "a status for the new version", cancellationToken: TestContext.Current.CancellationToken);
        hub.Statuses[1].Status.ShouldBe(ElevatorStatus.OutOfService);
        hub.Activities[^1].Kind.ShouldBe(ElevatorActivityKind.OutOfService);
        hub.Attempts[^1].ShouldBeOfType<ElevatorStatusDto>("the status follows the activity that changed it");
    }

    [Fact]
    public async Task Broadcast_ASpeedChange_SendsTheStatusAgain_ThoughTheVersionIsTheSame()
    {
        var hub = new FakeHubContext();
        await StartAsync(hub);
        PublishDoorUpdates(version: 3, 1);
        await Eventually.HoldsAsync(() => hub.Statuses.Count == 1, "the first status", cancellationToken: TestContext.Current.CancellationToken);

        var faster = Update(ElevatorActivityKind.SpeedChanged, 1, version: 3);
        _buffer.Publish(faster with { Status = faster.Status with { Speed = 8 } });

        await Eventually.HoldsAsync(() => hub.Statuses.Count == 2, "the status at the new speed", cancellationToken: TestContext.Current.CancellationToken);
        hub.Statuses[1].Speed.ShouldBe(8);
    }

    [Fact]
    public async Task Broadcast_ASchedulingChange_SendsTheStatusAgain_ThoughTheVersionIsTheSame()
    {
        var hub = new FakeHubContext();
        await StartAsync(hub);
        PublishDoorUpdates(version: 3, 1);
        await Eventually.HoldsAsync(() => hub.Statuses.Count == 1, "the first status", cancellationToken: TestContext.Current.CancellationToken);

        var switched = Update(ElevatorActivityKind.SchedulingChanged, 1, version: 3);
        _buffer.Publish(switched with { Status = switched.Status with { Scheduling = Scheduling.Look } });

        await Eventually.HoldsAsync(() => hub.Statuses.Count == 2, "the status with the new scheduling", cancellationToken: TestContext.Current.CancellationToken);
        hub.Statuses[1].Scheduling.ShouldBe(Scheduling.Look);
    }

    [Fact]
    public async Task Broadcast_OccupancyUpdates_SendTheNewestCountsOncePerBatch()
    {
        var hub = new FakeHubContext();
        _buffer.Publish(new OccupancyUpdateV1(Guid.CreateVersion7(), Occupancy(inCar: 1)));
        _buffer.Publish(new OccupancyUpdateV1(Guid.CreateVersion7(), Occupancy(inCar: 2)));

        await StartAsync(hub);

        await Eventually.HoldsAsync(() => hub.Attempts.OfType<BuildingOccupancyDto>().Any(), "the counts", cancellationToken: TestContext.Current.CancellationToken);
        hub.Attempts.OfType<BuildingOccupancyDto>().Single().PassengersInCar.ShouldBe(2);
    }

    [Fact]
    public async Task Broadcast_WhenSendingFails_LogsAndCarriesOnWithTheNextMessages()
    {
        var hub = FakeHubContext.Throwing();
        PublishDoorUpdates(version: 1, 1, 2, 3);

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

    internal static ElevatorUpdateV1 Update(ElevatorActivityKind kind, int floor, long version, ElevatorStatus status = ElevatorStatus.DoorOpen) =>
        new(
            Guid.CreateVersion7(),
            new ElevatorActivityDto(ApiFactory.StartTime, kind, floor),
            new ElevatorStatusDto(floor, status, null, null, [], [], version, ApiFactory.StartTime));

    internal static BuildingOccupancyDto Occupancy(int inCar) =>
        new(inCar, 8, [.. Enumerable.Range(1, 10).Select(floor => new FloorOccupancyDto(floor, 0, 0))], 0, null, ApiFactory.StartTime);

    private async Task StartAsync(FakeHubContext hub)
    {
        _service = new ElevatorBroadcastService(_buffer, hub, _logger);
        await _service.StartAsync(TestContext.Current.CancellationToken);
    }

    private void PublishDoorUpdates(long version, params int[] floors)
    {
        foreach (var floor in floors)
        {
            _buffer.Publish(Update(ElevatorActivityKind.DoorsOpened, floor, version));
        }
    }
}
