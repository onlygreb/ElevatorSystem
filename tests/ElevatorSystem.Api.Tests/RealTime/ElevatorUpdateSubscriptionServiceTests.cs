using ElevatorSystem.Api.RealTime;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace ElevatorSystem.Api.Tests.RealTime;

public sealed class ElevatorUpdateSubscriptionServiceTests
{
    private readonly FakeTimeProvider _time = new(ApiFactory.StartTime);
    private readonly RecordingSubscriber _subscriber = new();

    [Fact]
    public async Task StartAsync_HasSubscribed_BeforeItReturns()
    {
        using var service = Service();

        await service.StartAsync(TestContext.Current.CancellationToken);

        // An in-process engine starts right after this; the in-memory bus would drop anything published before.
        _subscriber.Subscribed.ShouldBeTrue("the subscription must not wait for ExecuteAsync on the thread pool");
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StopAsync_EndsTheSubscription()
    {
        using var service = Service();
        await service.StartAsync(TestContext.Current.CancellationToken);

        await service.StopAsync(TestContext.Current.CancellationToken);

        _subscriber.Ended.ShouldBeTrue();
    }

    private ElevatorUpdateSubscriptionService Service() =>
        new(
            _subscriber,
            new ElevatorStateCache(_time),
            new ElevatorEventBuffer(Options.Create(new RealTimeOptions { EventBufferCapacity = 16 }), _time, NullLogger<ElevatorEventBuffer>.Instance));

    /// <summary>Registers synchronously, like the in-memory bus, then waits until cancelled.</summary>
    private sealed class RecordingSubscriber : IElevatorUpdateSubscriber
    {
        public bool Subscribed { get; private set; }

        public bool Ended { get; private set; }

        public async Task SubscribeAsync(
            Func<ElevatorUpdateV1, CancellationToken, Task> onElevatorUpdate,
            Func<OccupancyUpdateV1, CancellationToken, Task> onOccupancyUpdate,
            CancellationToken cancellationToken)
        {
            Subscribed = true;
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                Ended = true;
            }
        }
    }
}
