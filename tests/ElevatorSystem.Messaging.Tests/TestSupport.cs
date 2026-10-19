using ElevatorSystem.Contracts;

namespace ElevatorSystem.Messaging.Tests;

/// <summary>
/// Polls a condition with a real-time guard. Integration tests never sleep for a fixed time: they wait for the outcome
/// and fail with a description of what never happened.
/// </summary>
internal static class Eventually
{
    public static readonly TimeSpan DefaultGuard = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public static async Task<T> ReturnsAsync<T>(Func<Task<T?>> probe, string description, TimeSpan? guard = null)
        where T : class
    {
        var deadline = TimeProvider.System.GetTimestamp() + (long)((guard ?? DefaultGuard).TotalSeconds * TimeProvider.System.TimestampFrequency);
        while (true)
        {
            if (await probe() is { } result)
            {
                return result;
            }

            if (TimeProvider.System.GetTimestamp() > deadline)
            {
                throw new TimeoutException($"Timed out after {guard ?? DefaultGuard} waiting for {description}.");
            }

            await Task.Delay(PollInterval, TestContext.Current.CancellationToken);
        }
    }

    public static Task IsTrueAsync(Func<Task<bool>> condition, string description, TimeSpan? guard = null) =>
        ReturnsAsync(async () => await condition() ? description : null, description, guard);

    public static Task IsTrueAsync(Func<bool> condition, string description, TimeSpan? guard = null) =>
        IsTrueAsync(() => Task.FromResult(condition()), description, guard);
}

/// <summary>Records what a handler received; tests wait for what they expect.</summary>
internal sealed class Inbox<T>
{
    private readonly Lock _gate = new();
    private readonly List<T> _messages = [];

    public IReadOnlyList<T> Messages
    {
        get
        {
            lock (_gate)
            {
                return [.. _messages];
            }
        }
    }

    public Task Handle(T message, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _messages.Add(message);
        }

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<T>> WaitForAsync(Func<IReadOnlyList<T>, bool> condition, string description, TimeSpan? guard = null)
    {
        await Eventually.IsTrueAsync(() => condition(Messages), $"{description} (received {Messages.Count})", guard);
        return Messages;
    }

    public Task<IReadOnlyList<T>> WaitForCountAsync(int count, TimeSpan? guard = null) =>
        WaitForAsync(messages => messages.Count >= count, $"{count} message(s)", guard);
}

internal static class TestMessages
{
    public static readonly DateTimeOffset IssuedAt = new(2026, 1, 5, 8, 0, 0, TimeSpan.Zero);

    public static HallCallCommandV1 Hall(int floor, CallDirection direction = CallDirection.Up) =>
        new(Guid.CreateVersion7(), IssuedAt, floor, direction);

    public static CarCallCommandV1 Car(int floor) => new(Guid.CreateVersion7(), IssuedAt, floor);

    public static ServiceCommandV1 Service(ServiceAction action) => new(Guid.CreateVersion7(), IssuedAt, action);

    public static ElevatorUpdateV1 ElevatorUpdate(int floor = 3) => new(
        Guid.CreateVersion7(),
        new ElevatorActivityDto(IssuedAt, ElevatorActivityKind.DoorsOpened, floor),
        new ElevatorStatusDto(floor, ElevatorStatus.DoorOpen, null, null, [], [], 1, IssuedAt));

    public static OccupancyUpdateV1 OccupancyUpdate() => new(
        Guid.CreateVersion7(),
        new BuildingOccupancyDto(1, 8, [new FloorOccupancyDto(3, 0, 1)], 0, null, IssuedAt));
}
