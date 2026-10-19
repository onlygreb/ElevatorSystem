using ElevatorSystem.Contracts;

namespace ElevatorSystem.Web.Tests;

/// <summary>Builders for contract objects, with the values of the real building (floors 1–10) as defaults.</summary>
internal static class TestData
{
    public static readonly DateTimeOffset Noon = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    public static ElevatorConfigurationDto Configuration(
        int lowestFloor = 1,
        int highestFloor = 10,
        double travelSecondsPerFloor = 1.5,
        double doorOpenSeconds = 3) =>
        new(lowestFloor, highestFloor, TimeSpan.FromSeconds(travelSecondsPerFloor), TimeSpan.FromSeconds(doorOpenSeconds), "Fifo");

    public static ElevatorStatusDto Status(
        int floor = 1,
        ElevatorStatus status = ElevatorStatus.Idle,
        long version = 1,
        OutOfServiceCause? cause = null,
        CallDirection? travelDirection = null,
        params PendingCallDto[] pending) =>
        new(floor, status, travelDirection, cause, pending, [.. pending.Select(call => call.Call.Floor).Distinct()], version, Noon);

    public static PendingCallDto HallCall(int floor, CallDirection direction, int requestCount = 1) =>
        new(new CallDto(CallKind.Hall, floor, direction), requestCount, Noon);

    public static PendingCallDto CarCall(int floor, int requestCount = 1) =>
        new(new CallDto(CallKind.Car, floor, null), requestCount, Noon);

    public static ElevatorActivityDto DoorsOpened(int floor, int secondsAfterNoon = 0) =>
        new(Noon.AddSeconds(secondsAfterNoon), ElevatorActivityKind.DoorsOpened, floor);

    public static ElevatorActivityDto Moved(int from, int to, int secondsAfterNoon = 0) =>
        new(Noon.AddSeconds(secondsAfterNoon), ElevatorActivityKind.Moved, to,
            FromFloor: from, Direction: to > from ? CallDirection.Up : CallDirection.Down);
}
