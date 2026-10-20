using ElevatorSystem.Api.RealTime;
using ElevatorSystem.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace ElevatorSystem.Api.Tests.RealTime;

public sealed class ElevatorEventBufferTests
{
    private readonly FakeTimeProvider _time = new(ApiFactory.StartTime);
    private readonly FakeLogger<ElevatorEventBuffer> _logger = new();

    [Fact]
    public void Publish_WithinCapacity_KeepsEveryEventInOrder()
    {
        var buffer = CreateBuffer(capacity: 3);

        Publish(buffer, 1, 2, 3);

        ReadAll(buffer).ShouldBe([1, 2, 3]);
        buffer.DroppedCount.ShouldBe(0);
    }

    [Fact]
    public void Publish_WhenFull_DropsTheOldestEventAndCountsIt()
    {
        var buffer = CreateBuffer(capacity: 3);

        Publish(buffer, 1, 2, 3, 4, 5);

        ReadAll(buffer).ShouldBe([3, 4, 5], "the newest updates matter most to clients catching up");
        buffer.DroppedCount.ShouldBe(2);
    }

    [Fact]
    public void Publish_WhenFull_LogsTheDropsAtMostOncePerInterval()
    {
        var buffer = CreateBuffer(capacity: 1);

        Publish(buffer, 1, 2, 3, 4);
        _time.Advance(ElevatorEventBuffer.DropLogInterval);
        Publish(buffer, 5);

        var warnings = _logger.Collector.GetSnapshot();
        warnings.Count.ShouldBe(2, "a flood of drops must not flood the log as well");
        warnings.ShouldAllBe(record => record.Level == LogLevel.Warning && record.Id.Id == 3000);
        warnings[0].StructuredState!.ShouldContain(new KeyValuePair<string, string?>("TotalDropped", "1"));
        warnings[1].StructuredState!.ShouldContain(new KeyValuePair<string, string?>("DroppedSinceLastReport", "3"));
        warnings[1].StructuredState!.ShouldContain(new KeyValuePair<string, string?>("TotalDropped", "4"));
    }

    private static void Publish(ElevatorEventBuffer buffer, params int[] floors)
    {
        foreach (var floor in floors)
        {
            buffer.Publish(ElevatorBroadcastServiceTests.Update(ElevatorActivityKind.DoorsOpened, floor, version: 1));
        }
    }

    private static List<int> ReadAll(ElevatorEventBuffer buffer)
    {
        var floors = new List<int>();
        while (buffer.Reader.TryRead(out var update))
        {
            floors.Add(((ElevatorUpdateV1)update).Activity.Floor);
        }

        return floors;
    }

    private ElevatorEventBuffer CreateBuffer(int capacity) =>
        new(Options.Create(new RealTimeOptions { EventBufferCapacity = capacity }), _time, _logger);
}
