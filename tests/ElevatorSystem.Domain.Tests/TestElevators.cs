using Microsoft.Extensions.Time.Testing;

namespace ElevatorSystem.Domain.Tests;

/// <summary>Builders for elevators, calls and requests used across the domain tests.</summary>
internal static class TestElevators
{
    public static readonly DateTimeOffset Start = new(2026, 1, 5, 8, 0, 0, TimeSpan.Zero);

    public static FakeTimeProvider Clock() => new(Start);

    public static Elevator At(int floor, TimeProvider? time = null) =>
        new(Building.Default, time ?? Clock(), new Floor(floor));

    /// <summary>An elevator brought into <paramref name="state"/> through its public transitions, at <paramref name="floor"/>.</summary>
    public static Elevator InState(ElevatorState state, int floor = 5)
    {
        switch (state)
        {
            case ElevatorState.Idle:
                return At(floor);
            case ElevatorState.MovingUp:
                var movingUp = At(floor - 1);
                movingUp.MoveUp().Succeeded.ShouldBeTrue();
                return movingUp;
            case ElevatorState.MovingDown:
                var movingDown = At(floor + 1);
                movingDown.MoveDown().Succeeded.ShouldBeTrue();
                return movingDown;
            case ElevatorState.DoorOpen:
                var doorOpen = At(floor);
                doorOpen.OpenDoor().Succeeded.ShouldBeTrue();
                return doorOpen;
            case ElevatorState.OutOfService:
                var outOfService = At(floor);
                outOfService.TakeOutOfService(OutOfServiceReason.Maintenance).Succeeded.ShouldBeTrue();
                return outOfService;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }
    }

    public static HallCall Hall(int floor, Direction direction) => new(new Floor(floor), direction);

    public static CarCall Car(int floor) => new(new Floor(floor));

    public static PassengerRequest Request(ElevatorCall call, TimeProvider? time = null)
    {
        var clock = time ?? Clock();
        return new PassengerRequest(RequestId.New(clock), call, clock.GetUtcNow());
    }

    /// <summary>A snapshot of an idle car at <paramref name="floor"/> with <paramref name="calls"/> pending, oldest first.</summary>
    public static ElevatorSnapshot SnapshotAt(int floor, Direction? travelDirection, params ElevatorCall[] calls) =>
        new(
            new Floor(floor),
            ElevatorState.Idle,
            travelDirection,
            OutOfServiceReason: null,
            [.. calls.Select((call, index) => new PendingCall(call, RequestId.New(TimeProvider.System), Start.AddSeconds(index), 1))],
            Version: 1,
            TakenAt: Start);

    /// <summary>Every call that can legally be made in <paramref name="building"/>.</summary>
    public static IReadOnlyList<ElevatorCall> AllValidCalls(Building building)
    {
        var calls = new List<ElevatorCall>();
        for (var number = building.LowestFloor.Number; number <= building.HighestFloor.Number; number++)
        {
            var floor = new Floor(number);
            calls.Add(new CarCall(floor));
            foreach (var direction in Enum.GetValues<Direction>())
            {
                if (building.CanTravel(floor, direction))
                {
                    calls.Add(new HallCall(floor, direction));
                }
            }
        }

        return calls;
    }
}
