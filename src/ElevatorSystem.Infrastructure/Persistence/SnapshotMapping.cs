using ElevatorSystem.Domain;

namespace ElevatorSystem.Infrastructure.Persistence;

/// <summary>
/// Maps between <see cref="ElevatorSnapshot"/> and its persistence models. The stored codes are part
/// of the schema: change them only with a migration that rewrites existing rows.
/// </summary>
internal static class SnapshotMapping
{
    public static ElevatorStateRecord ToRecord(short elevatorId, ElevatorSnapshot snapshot) => new()
    {
        Id = elevatorId,
        CurrentFloor = snapshot.CurrentFloor.Number,
        State = ToCode(snapshot.State),
        TravelDirection = snapshot.TravelDirection is { } direction ? ToCode(direction) : null,
        OutOfServiceReason = snapshot.State == ElevatorState.OutOfService
            ? ToCode(snapshot.OutOfServiceReason ?? Domain.OutOfServiceReason.Fault)
            : null,
        Version = snapshot.Version,
        TakenAt = snapshot.TakenAt.ToUniversalTime(),
        PendingCalls = [.. snapshot.PendingCalls.Select((pending, position) => ToRecord(elevatorId, position, pending))],
    };

    public static ElevatorSnapshot ToSnapshot(ElevatorStateRecord record)
    {
        var state = ToState(record.State);
        return new ElevatorSnapshot(
            new Floor(record.CurrentFloor),
            state,
            record.TravelDirection is { } direction ? ToDirection(direction) : null,
            state == ElevatorState.OutOfService && record.OutOfServiceReason is { } reason ? ToReason(reason) : null,
            [.. record.PendingCalls.OrderBy(call => call.Position).Select(ToPendingCall)],
            record.Version,
            record.TakenAt);
    }

    private static PendingCallRecord ToRecord(short elevatorId, int position, PendingCall pending) => new()
    {
        ElevatorId = elevatorId,
        Position = position,
        Kind = pending.Call switch
        {
            HallCall => "hall",
            CarCall => "car",
            _ => throw new ArgumentException($"Unknown call type {pending.Call.GetType().Name}.", nameof(pending)),
        },
        Floor = pending.Call.Floor.Number,
        Direction = pending.Call is HallCall hall ? ToCode(hall.Direction) : null,
        FirstRequestId = pending.FirstRequestId.Value,
        FirstRequestedAt = pending.FirstRequestedAt.ToUniversalTime(),
        RequestCount = pending.RequestCount,
    };

    private static PendingCall ToPendingCall(PendingCallRecord record)
    {
        var floor = new Floor(record.Floor);
        ElevatorCall call = record.Kind switch
        {
            "hall" => new HallCall(floor, ToDirection(record.Direction ?? throw Corrupt("hall call without a direction"))),
            "car" => new CarCall(floor),
            _ => throw Corrupt($"unknown call kind '{record.Kind}'"),
        };

        return new PendingCall(call, new RequestId(record.FirstRequestId), record.FirstRequestedAt, record.RequestCount);
    }

    private static string ToCode(ElevatorState state) => state switch
    {
        ElevatorState.Idle => "idle",
        ElevatorState.MovingUp => "moving_up",
        ElevatorState.MovingDown => "moving_down",
        ElevatorState.DoorOpen => "door_open",
        ElevatorState.OutOfService => "out_of_service",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown elevator state."),
    };

    private static ElevatorState ToState(string code) => code switch
    {
        "idle" => ElevatorState.Idle,
        "moving_up" => ElevatorState.MovingUp,
        "moving_down" => ElevatorState.MovingDown,
        "door_open" => ElevatorState.DoorOpen,
        "out_of_service" => ElevatorState.OutOfService,
        _ => throw Corrupt($"unknown state '{code}'"),
    };

    private static string ToCode(Direction direction) => direction switch
    {
        Direction.Up => "up",
        Direction.Down => "down",
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown direction."),
    };

    private static Direction ToDirection(string code) => code switch
    {
        "up" => Direction.Up,
        "down" => Direction.Down,
        _ => throw Corrupt($"unknown direction '{code}'"),
    };

    private static string ToCode(OutOfServiceReason reason) => reason switch
    {
        Domain.OutOfServiceReason.Maintenance => "maintenance",
        Domain.OutOfServiceReason.Fault => "fault",
        Domain.OutOfServiceReason.EmergencyStop => "emergency_stop",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown out-of-service reason."),
    };

    private static OutOfServiceReason ToReason(string code) => code switch
    {
        "maintenance" => Domain.OutOfServiceReason.Maintenance,
        "fault" => Domain.OutOfServiceReason.Fault,
        "emergency_stop" => Domain.OutOfServiceReason.EmergencyStop,
        _ => throw Corrupt($"unknown out-of-service reason '{code}'"),
    };

    private static InvalidDataException Corrupt(string detail) => new($"The saved elevator state is invalid: {detail}.");
}
