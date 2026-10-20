
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ElevatorSystem.Application.Tests;

public sealed class CompositeElevatorEventSinkTests
{
    private static readonly ElevatorEvent Opened = new DoorsOpened(ControllerHarness.StartTime, new Floor(3));

    [Fact]
    public void Publish_HandsTheEventToEverySinkInOrder()
    {
        var order = new List<string>();
        var sink = new CompositeElevatorEventSink(
            [new NamedSink("first", order), new NamedSink("second", order), new NamedSink("third", order)],
            NullLogger<CompositeElevatorEventSink>.Instance);

        sink.Publish(Opened);

        order.ShouldBe(["first", "second", "third"]);
    }

    [Fact]
    public void Publish_SinkThatThrows_IsLoggedAndTheOthersStillReceiveTheEvent()
    {
        var before = new EventProbe();
        var after = new EventProbe();
        var log = new RecordingLogger<CompositeElevatorEventSink>();
        var sink = new CompositeElevatorEventSink([before, new ThrowingSink(new EventProbe()), after], log);

        sink.Publish(Opened);
        sink.Publish(Opened);

        before.Count.ShouldBe(2);
        after.Count.ShouldBe(2, "a failing sink must not starve the sinks after it");
        var entries = log.WithId(1517);
        entries.Count.ShouldBe(2);
        entries[0].Level.ShouldBe(LogLevel.Error);
        entries[0].Message.ShouldContain(nameof(ThrowingSink));
        entries[0].Exception.ShouldBeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task Publish_FromManyThreadsAtOnce_ReachesEverySinkEveryTime()
    {
        var first = new EventProbe();
        var second = new EventProbe();
        var sink = new CompositeElevatorEventSink([first, second], NullLogger<CompositeElevatorEventSink>.Instance);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(
                async () =>
                {
                    await start.Task;
                    for (var index = 0; index < 500; index++)
                    {
                        sink.Publish(Opened);
                    }
                },
                TestContext.Current.CancellationToken))
            .ToArray();

        start.SetResult();
        await Task.WhenAll(publishers);

        first.Count.ShouldBe(4_000);
        second.Count.ShouldBe(4_000);
    }

    [Fact]
    public void Constructor_WithAMissingSink_IsRefused()
    {
        IElevatorEventSink[] sinks = [new EventProbe(), null!];

        Should.Throw<ArgumentException>(() => new CompositeElevatorEventSink(sinks, NullLogger<CompositeElevatorEventSink>.Instance));
    }

    private sealed class NamedSink(string name, List<string> order) : IElevatorEventSink
    {
        public void Publish(ElevatorEvent elevatorEvent) => order.Add(name);
    }
}
