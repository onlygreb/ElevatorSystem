using ElevatorSystem.Api.RealTime;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ElevatorSystem.Api.Tests.RealTime;

/// <summary>
/// The real-time channel must never hold the elevator back (ADR-0007, ADR-0015): whatever SignalR does,
/// the car keeps serving passengers on time.
/// </summary>
public sealed class FanOutIsolationTests
{
    // A stalled control loop also blocks host shutdown, so a regression must fail on a timeout, not hang the run.
    private const int TimeoutMs = 30_000;

    [Fact(Timeout = TimeoutMs)]
    public async Task HangingClients_DoNotStallTheElevator_AndOverflowIsDropped()
    {
        var hub = FakeHubContext.Hanging();
        await using var factory = CreateFactory(hub);
        var controller = await factory.ControllerAsync();
        var buffer = factory.Services.GetRequiredService<ElevatorEventBuffer>();

        controller.RequestDestination(10).IsAccepted.ShouldBeTrue();

        await Eventually.HoldsAsync(
            () => controller.Elevator.Snapshot() is { CurrentFloor.Number: 10, State: ElevatorState.DoorOpen },
            "the car to reach floor 10 although no message could be delivered",
            factory.Time,
            ApiFactory.TravelTime,
            TestContext.Current.CancellationToken);
        hub.Attempts.Count.ShouldBe(1, "the broadcaster is stuck on its first send");
        buffer.DroppedCount.ShouldBeGreaterThan(0, "a buffer of 4 cannot hold a 9-floor trip's events");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task ThrowingClients_DoNotStallTheElevator_OrEndTheFeed()
    {
        var hub = FakeHubContext.Throwing();
        await using var factory = CreateFactory(hub);
        var controller = await factory.ControllerAsync();

        controller.RequestDestination(5).IsAccepted.ShouldBeTrue();

        await Eventually.HoldsAsync(
            () => controller.Elevator.Snapshot() is { CurrentFloor.Number: 5, State: ElevatorState.DoorOpen },
            "the car to reach floor 5",
            factory.Time,
            ApiFactory.TravelTime,
            TestContext.Current.CancellationToken);
        await Eventually.HoldsAsync(
            () => hub.Activities.Any(activity => activity.Kind == ElevatorActivityKind.DoorsOpened),
            "the broadcaster to keep trying after every failure",
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static ApiFactory CreateFactory(FakeHubContext hub)
    {
        var factory = new ApiFactory()
            .WithFakeTime()
            .WithSetting("RealTime:EventBufferCapacity", "4")
            .WithServices(services => services.Replace(
                ServiceDescriptor.Singleton<IHubContext<ElevatorStatusHub, IElevatorClient>>(hub)));
        _ = factory.Server;
        return factory;
    }
}
