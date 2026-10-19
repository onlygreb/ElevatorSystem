namespace ElevatorSystem.Web.Resources;

/// <summary>
/// Keys of <see cref="SharedResource"/>. Every user-facing string goes through these constants, and a test
/// checks that each one exists in both the en-US and the pt-BR .resx files.
/// </summary>
internal static class Text
{
    public const string AppTitle = nameof(AppTitle);
    public const string PageTitle = nameof(PageTitle);
    public const string LanguageLabel = nameof(LanguageLabel);
    public const string LanguageEnglish = nameof(LanguageEnglish);
    public const string LanguagePortuguese = nameof(LanguagePortuguese);
    public const string UnhandledError = nameof(UnhandledError);
    public const string Reload = nameof(Reload);
    public const string Dismiss = nameof(Dismiss);
    public const string NotFoundTitle = nameof(NotFoundTitle);
    public const string NotFoundText = nameof(NotFoundText);
    public const string BackHome = nameof(BackHome);
    public const string Loading = nameof(Loading);
    public const string ConfigurationFailed = nameof(ConfigurationFailed);
    public const string TryAgain = nameof(TryAgain);
    public const string Connecting = nameof(Connecting);
    public const string Reconnecting = nameof(Reconnecting);
    public const string FloorsHeading = nameof(FloorsHeading);
    public const string CarPanelHeading = nameof(CarPanelHeading);
    public const string CarPanelHint = nameof(CarPanelHint);
    public const string QueueHeading = nameof(QueueHeading);
    public const string QueueHint = nameof(QueueHint);
    public const string QueueEmpty = nameof(QueueEmpty);
    public const string ActivityHeading = nameof(ActivityHeading);
    public const string ActivityEmpty = nameof(ActivityEmpty);
    public const string FloorLabel = nameof(FloorLabel);
    public const string HallButtonsLabel = nameof(HallButtonsLabel);
    public const string CallUpLabel = nameof(CallUpLabel);
    public const string CallDownLabel = nameof(CallDownLabel);
    public const string SelectFloorLabel = nameof(SelectFloorLabel);
    public const string CarDescription = nameof(CarDescription);
    public const string DoorsOpen = nameof(DoorsOpen);
    public const string DoorsClosed = nameof(DoorsClosed);
    public const string StatusUnknown = nameof(StatusUnknown);
    public const string StatusIdle = nameof(StatusIdle);
    public const string StatusMovingUp = nameof(StatusMovingUp);
    public const string StatusMovingDown = nameof(StatusMovingDown);
    public const string StatusDoorOpen = nameof(StatusDoorOpen);
    public const string StatusOutOfService = nameof(StatusOutOfService);
    public const string CauseMaintenance = nameof(CauseMaintenance);
    public const string CauseFault = nameof(CauseFault);
    public const string CauseEmergencyStop = nameof(CauseEmergencyStop);
    public const string OutOfServiceCause = nameof(OutOfServiceCause);
    public const string QueueHallUp = nameof(QueueHallUp);
    public const string QueueHallDown = nameof(QueueHallDown);
    public const string QueueCar = nameof(QueueCar);
    public const string QueueMerged = nameof(QueueMerged);
    public const string CallHallUp = nameof(CallHallUp);
    public const string CallHallDown = nameof(CallHallDown);
    public const string CallCar = nameof(CallCar);
    public const string ActivityCallQueued = nameof(ActivityCallQueued);
    public const string ActivityCallMerged = nameof(ActivityCallMerged);
    public const string ActivityRequestRejected = nameof(ActivityRequestRejected);
    public const string ActivityMoved = nameof(ActivityMoved);
    public const string ActivityDoorsOpened = nameof(ActivityDoorsOpened);
    public const string ActivityCallServed = nameof(ActivityCallServed);
    public const string ActivityCallServedMerged = nameof(ActivityCallServedMerged);
    public const string ActivityDoorsClosed = nameof(ActivityDoorsClosed);
    public const string ActivityOutOfService = nameof(ActivityOutOfService);
    public const string ActivityReturnedToService = nameof(ActivityReturnedToService);
    public const string AnnounceDoorsOpening = nameof(AnnounceDoorsOpening);
    public const string AnnounceOutOfService = nameof(AnnounceOutOfService);
    public const string AnnounceBackInService = nameof(AnnounceBackInService);
    public const string RequestRefusedTitle = nameof(RequestRefusedTitle);
    public const string RefusedInvalidFloor = nameof(RefusedInvalidFloor);
    public const string RefusedInvalidDirection = nameof(RefusedInvalidDirection);
    public const string RefusedOutOfService = nameof(RefusedOutOfService);
    public const string RefusedBusy = nameof(RefusedBusy);
    public const string RefusedTooManyRequests = nameof(RefusedTooManyRequests);
    public const string RefusedUnavailable = nameof(RefusedUnavailable);
    public const string RefusedUnexpected = nameof(RefusedUnexpected);
    public const string FloorCaption = nameof(FloorCaption);
    public const string FloorsHint = nameof(FloorsHint);
}
