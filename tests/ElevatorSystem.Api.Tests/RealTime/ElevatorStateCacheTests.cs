using ElevatorSystem.Api.RealTime;
using ElevatorSystem.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace ElevatorSystem.Api.Tests.RealTime;

public sealed class ElevatorStateCacheTests
{
    private readonly ElevatorStateCache _cache = new(new FakeTimeProvider(ApiFactory.StartTime));

    [Fact]
    public void Empty_KnowsNothing()
    {
        _cache.Status.ShouldBeNull();
        _cache.Occupancy.ShouldBeNull();
        _cache.LastUpdateAt.ShouldBeNull();
    }

    [Fact]
    public void TryApply_KeepsTheLatestUpdate_InArrivalOrder()
    {
        _cache.TryApply(ElevatorBroadcastServiceTests.Update(ElevatorActivityKind.DoorsOpened, 3, version: 9)).ShouldBeTrue();
        _cache.TryApply(ElevatorBroadcastServiceTests.Update(ElevatorActivityKind.DoorsOpened, 4, version: 2)).ShouldBeTrue();

        _cache.Status.ShouldNotBeNull().CurrentFloor.ShouldBe(4, "only the leader publishes, so arrival order is the engine's order");
    }

    [Fact]
    public void TryApply_ACopyOfAnUpdate_IsIgnored()
    {
        var update = ElevatorBroadcastServiceTests.Update(ElevatorActivityKind.DoorsOpened, 3, version: 1);
        _cache.TryApply(update);
        _cache.TryApply(ElevatorBroadcastServiceTests.Update(ElevatorActivityKind.DoorsClosed, 3, version: 2));

        var applied = _cache.TryApply(update);

        applied.ShouldBeFalse("a redelivered or re-sent update is recognised by its id");
        _cache.Status.ShouldNotBeNull().Version.ShouldBe(2);
        _cache.LastUpdateAt.ShouldNotBeNull("a copy still shows the engine is alive");
    }

    [Fact]
    public void TryApply_Occupancy_KeepsTheLatestCounts()
    {
        var update = new OccupancyUpdateV1(Guid.CreateVersion7(), ElevatorBroadcastServiceTests.Occupancy(inCar: 3));

        _cache.TryApply(update).ShouldBeTrue();
        _cache.TryApply(update).ShouldBeFalse();

        _cache.Occupancy.ShouldNotBeNull().PassengersInCar.ShouldBe(3);
    }
}
