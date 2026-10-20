using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;

namespace ElevatorSystem.Application;

/// <summary>
/// Translates between the domain and the wire contracts. Enums are mapped by name, never by number,
/// so reordering either side cannot silently change the meaning of a message.
/// </summary>
public static class ElevatorContractMapper
{
    /// <param name="snapshot">The elevator's state.</param>
    /// <param name="doorsCloseAt">The controller's door countdown (<see cref="ElevatorController.DoorsCloseAt"/>); ignored unless the door is open.</param>
    /// <param name="speed">The controller's <see cref="ElevatorController.Speed"/>.</param>
    /// <param name="scheduling">The controller's <see cref="ElevatorController.Scheduling"/>, when known.</param>
    public static ElevatorStatusDto ToStatus(ElevatorSnapshot snapshot, DateTimeOffset? doorsCloseAt = null, double speed = 1, SchedulingPolicy? scheduling = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new ElevatorStatusDto(
            snapshot.CurrentFloor.Number,
            ToContract(snapshot.State),
            snapshot.TravelDirection is { } direction ? ToContract(direction) : null,
            snapshot.OutOfServiceReason is { } reason ? ToContract(reason) : null,
            [.. snapshot.PendingCalls.Select(ToContract)],
            [.. snapshot.TargetFloors.Select(floor => floor.Number)],
            snapshot.Version,
            snapshot.TakenAt,
            snapshot.State == ElevatorState.DoorOpen ? doorsCloseAt : null,
            speed,
            scheduling is { } policy ? ToContract(policy) : null);
    }

    public static ElevatorConfigurationDto ToConfiguration(ElevatorController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);

        var building = controller.Elevator.Building;
        return new ElevatorConfigurationDto(
            building.LowestFloor.Number,
            building.HighestFloor.Number,
            controller.Options.TravelTimePerFloor,
            controller.Options.DoorOpenDuration,
            controller.Strategy.Name);
    }

    public static ElevatorActivityDto ToActivity(ElevatorEvent elevatorEvent)
    {
        ArgumentNullException.ThrowIfNull(elevatorEvent);

        var at = elevatorEvent.OccurredAt;
        var floor = elevatorEvent.Floor.Number;
        return elevatorEvent switch
        {
            CallQueued queued => new(at, ElevatorActivityKind.CallQueued, floor,
                Call: ToContract(queued.Request.Call), RequestId: queued.Request.Id.Value, Position: queued.Position),
            CallMerged merged => new(at, ElevatorActivityKind.CallMerged, floor,
                Call: ToContract(merged.Request.Call), RequestId: merged.Request.Id.Value, Position: merged.Position),
            RequestRejected rejected => new(at, ElevatorActivityKind.RequestRejected, floor,
                Call: ToContract(rejected.Request.Call), RequestId: rejected.Request.Id.Value, RejectionReason: ToContract(rejected.Reason)),
            ElevatorMoved moved => new(at, ElevatorActivityKind.Moved, floor,
                FromFloor: moved.From.Number, Direction: ToContract(moved.Direction)),
            DoorsOpened => new(at, ElevatorActivityKind.DoorsOpened, floor),
            CallServed served => new(at, ElevatorActivityKind.CallServed, floor,
                Call: ToContract(served.Call.Call), RequestId: served.Call.FirstRequestId.Value,
                RequestCount: served.Call.RequestCount, WaitTime: served.WaitTime),
            DoorsClosed => new(at, ElevatorActivityKind.DoorsClosed, floor),
            ElevatorTakenOutOfService outOfService => new(at, ElevatorActivityKind.OutOfService, floor,
                OutOfServiceCause: ToContract(outOfService.Reason), CancelledCalls: outOfService.CancelledCalls.Length),
            RequestsCleared cleared => new(at, ElevatorActivityKind.RequestsCleared, floor, CancelledCalls: cleared.CancelledCalls.Length),
            ElevatorReturnedToService => new(at, ElevatorActivityKind.ReturnedToService, floor),
            _ => throw new ArgumentOutOfRangeException(nameof(elevatorEvent), elevatorEvent.GetType().Name, "Unmapped elevator event."),
        };
    }

    public static Scheduling ToContract(SchedulingPolicy policy) => policy switch
    {
        SchedulingPolicy.Fifo => Scheduling.Fifo,
        SchedulingPolicy.Look => Scheduling.Look,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null),
    };

    public static SchedulingPolicy ToDomain(Scheduling scheduling) => scheduling switch
    {
        Scheduling.Fifo => SchedulingPolicy.Fifo,
        Scheduling.Look => SchedulingPolicy.Look,
        _ => throw new ArgumentOutOfRangeException(nameof(scheduling), scheduling, null),
    };

    public static Direction ToDomain(CallDirection direction) => direction switch
    {
        CallDirection.Up => Direction.Up,
        CallDirection.Down => Direction.Down,
        _ => (Direction)(-1), // Unknown values stay invalid so the domain refuses them as InvalidDirection.
    };

    public static RejectionReason ToContract(RequestRejectionReason reason) => reason switch
    {
        RequestRejectionReason.InvalidFloor => RejectionReason.InvalidFloor,
        RequestRejectionReason.InvalidDirection => RejectionReason.InvalidDirection,
        RequestRejectionReason.OutOfService => RejectionReason.OutOfService,
        RequestRejectionReason.Busy => RejectionReason.Busy,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    public static CallDirection ToContract(Direction direction) => direction switch
    {
        Direction.Up => CallDirection.Up,
        Direction.Down => CallDirection.Down,
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null),
    };

    public static ElevatorStatus ToContract(ElevatorState state) => state switch
    {
        ElevatorState.Idle => ElevatorStatus.Idle,
        ElevatorState.MovingUp => ElevatorStatus.MovingUp,
        ElevatorState.MovingDown => ElevatorStatus.MovingDown,
        ElevatorState.DoorOpen => ElevatorStatus.DoorOpen,
        ElevatorState.OutOfService => ElevatorStatus.OutOfService,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    public static OutOfServiceCause ToContract(OutOfServiceReason reason) => reason switch
    {
        OutOfServiceReason.Maintenance => OutOfServiceCause.Maintenance,
        OutOfServiceReason.Fault => OutOfServiceCause.Fault,
        OutOfServiceReason.EmergencyStop => OutOfServiceCause.EmergencyStop,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    public static CallDto ToContract(ElevatorCall call) => call switch
    {
        HallCall hall => new CallDto(CallKind.Hall, hall.Floor.Number, ToContract(hall.Direction)),
        CarCall car => new CallDto(CallKind.Car, car.Floor.Number, null),
        _ => throw new ArgumentOutOfRangeException(nameof(call), call?.GetType().Name, "Unmapped call type."),
    };

    private static PendingCallDto ToContract(PendingCall pending) =>
        new(ToContract(pending.Call), pending.RequestCount, pending.FirstRequestedAt);
}
