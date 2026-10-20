using System.Globalization;
using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Resources;
using ElevatorSystem.Web.Services;
using Microsoft.Extensions.Localization;

namespace ElevatorSystem.Web.Features.Passenger;

/// <summary>Turns contract values into localised words, so every component describes the elevator the same way.</summary>
internal static class Narration
{
    public static string State(this IStringLocalizer<SharedResource> text, ElevatorStatus status) => text[status switch
    {
        ElevatorStatus.Idle => Text.StatusIdle,
        ElevatorStatus.MovingUp => Text.StatusMovingUp,
        ElevatorStatus.MovingDown => Text.StatusMovingDown,
        ElevatorStatus.DoorOpen => Text.StatusDoorOpen,
        ElevatorStatus.OutOfService => Text.StatusOutOfService,
        _ => Text.StatusUnknown,
    }];

    public static string Cause(this IStringLocalizer<SharedResource> text, OutOfServiceCause? cause) => text[cause switch
    {
        OutOfServiceCause.Maintenance => Text.CauseMaintenance,
        OutOfServiceCause.Fault => Text.CauseFault,
        OutOfServiceCause.EmergencyStop => Text.CauseEmergencyStop,
        _ => Text.StatusOutOfService,
    }];

    /// <summary>A call inside a sentence: "floor 3 going up", "floor 5 from the car".</summary>
    public static string Call(this IStringLocalizer<SharedResource> text, CallDto? call) => call switch
    {
        { Kind: CallKind.Hall, Direction: CallDirection.Up } => text[Text.CallHallUp, call.Floor],
        { Kind: CallKind.Hall, Direction: CallDirection.Down } => text[Text.CallHallDown, call.Floor],
        { Kind: CallKind.Car } => text[Text.CallCar, call.Floor],
        _ => text[Text.FloorLabel, string.Empty],
    };

    /// <summary>A call on its own, as a queue chip reads: "Floor 7, going up".</summary>
    public static string QueuedCall(this IStringLocalizer<SharedResource> text, CallDto call) => call switch
    {
        { Kind: CallKind.Hall, Direction: CallDirection.Up } => text[Text.QueueHallUp, call.Floor],
        { Kind: CallKind.Hall, Direction: CallDirection.Down } => text[Text.QueueHallDown, call.Floor],
        _ => text[Text.QueueCar, call.Floor],
    };

    public static string Refusal(this IStringLocalizer<SharedResource> text, RejectionReason? reason) =>
        text[reason is { } known ? ElevatorApiClient.MessageKeyFor(known) : Text.RefusedUnexpected];

    /// <summary>One line of the activity feed.</summary>
    public static string Describe(this IStringLocalizer<SharedResource> text, ElevatorActivityDto activity) => activity.Kind switch
    {
        ElevatorActivityKind.CallQueued => text[Text.ActivityCallQueued, text.Call(activity.Call), activity.Position ?? 0],
        ElevatorActivityKind.CallMerged => text[Text.ActivityCallMerged, text.Call(activity.Call), activity.Position ?? 0],
        ElevatorActivityKind.RequestRejected => text[Text.ActivityRequestRejected, text.Call(activity.Call), text.Refusal(activity.RejectionReason)],
        ElevatorActivityKind.Moved => text[Text.ActivityMoved, activity.FromFloor ?? activity.Floor, activity.Floor],
        ElevatorActivityKind.DoorsOpened => text[Text.ActivityDoorsOpened, activity.Floor],
        ElevatorActivityKind.CallServed when activity.RequestCount > 1 =>
            text[Text.ActivityCallServedMerged, text.Call(activity.Call), Seconds(activity.WaitTime), activity.RequestCount],
        ElevatorActivityKind.CallServed => text[Text.ActivityCallServed, text.Call(activity.Call), Seconds(activity.WaitTime)],
        ElevatorActivityKind.DoorsClosed => text[Text.ActivityDoorsClosed, activity.Floor],
        ElevatorActivityKind.OutOfService =>
            text[Text.ActivityOutOfService, activity.Floor, text.Cause(activity.OutOfServiceCause), activity.CancelledCalls ?? 0],
        ElevatorActivityKind.ReturnedToService => text[Text.ActivityReturnedToService, activity.Floor],
        ElevatorActivityKind.PassengerJoined => text[Text.ActivityPassengerJoined, activity.Floor],
        ElevatorActivityKind.PassengerCalled => text[activity.Direction == CallDirection.Down ? Text.ActivityPassengerCalledDown : Text.ActivityPassengerCalledUp, activity.Floor],
        ElevatorActivityKind.PassengerBoarded => text[Text.ActivityPassengerBoarded, activity.Floor],
        ElevatorActivityKind.PassengerChoseFloor => text[Text.ActivityPassengerChoseFloor, activity.Floor],
        ElevatorActivityKind.PassengerLeft => text[Text.ActivityPassengerLeft, activity.Floor],
        ElevatorActivityKind.PassengerRemoved => text[Text.ActivityPassengerRemoved, activity.Floor],
        ElevatorActivityKind.PassengerRefused => text[Text.ActivityPassengerRefused, activity.Floor],
        ElevatorActivityKind.SpeedChanged when activity.Speed > 1 =>
            text[Text.ActivitySpeedFast, activity.Speed.Value.ToString("0.#", CultureInfo.CurrentCulture)],
        ElevatorActivityKind.SpeedChanged => text[Text.ActivitySpeedNormal],
        ElevatorActivityKind.RequestsCleared => text[Text.ActivityRequestsCleared, activity.CancelledCalls ?? 0],
        ElevatorActivityKind.SchedulingChanged => text[Text.ActivitySchedulingChanged, activity.Scheduling?.ToString().ToUpperInvariant() ?? string.Empty],
        _ => activity.Kind.ToString(),
    };

    /// <summary>What the car would say out loud for this step, or null when it stays quiet.</summary>
    public static string? Announcement(this IStringLocalizer<SharedResource> text, ElevatorActivityDto activity) => activity.Kind switch
    {
        ElevatorActivityKind.DoorsOpened => text[Text.AnnounceDoorsOpening, activity.Floor].Value,
        ElevatorActivityKind.OutOfService => text[Text.AnnounceOutOfService, text.Cause(activity.OutOfServiceCause)].Value,
        ElevatorActivityKind.ReturnedToService => text[Text.AnnounceBackInService, activity.Floor].Value,
        _ => null,
    };

    /// <summary>The wall-clock time of an activity in the reader's culture and time zone.</summary>
    public static string Time(ElevatorActivityDto activity) =>
        activity.OccurredAt.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);

    private static double Seconds(TimeSpan? duration) => (duration ?? TimeSpan.Zero).TotalSeconds;
}
