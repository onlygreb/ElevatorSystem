using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Resources;
using ElevatorSystem.Web.Services;
using QRCoder;

namespace ElevatorSystem.Web.Features.Admin;

/// <summary>Words and pictures shared by the admin components.</summary>
internal static class StaffText
{
    /// <summary>The <see cref="Text"/> key that explains <paramref name="failure"/>.</summary>
    public static string For(StaffFailure failure) => failure switch
    {
        StaffFailure.InvalidCredentials => Text.SignInFailedCredentials,
        StaffFailure.TwoFactorRequired => Text.SignInTwoFactorRequired,
        StaffFailure.InvalidTwoFactorCode => Text.SignInInvalidCode,
        StaffFailure.SessionExpired => Text.StaffSessionExpired,
        StaffFailure.NotAllowed => Text.StaffNotAllowed,
        StaffFailure.TooManyRequests => Text.StaffTooManyRequests,
        StaffFailure.Unavailable => Text.StaffUnavailable,
        _ => Text.StaffUnexpected,
    };

    public static string For(ServiceAction action) => action switch
    {
        ServiceAction.StartMaintenance => Text.ActionStartMaintenance,
        ServiceAction.EmergencyStop => Text.ActionEmergencyStop,
        ServiceAction.ReturnToService => Text.ActionReturnToService,
        _ => Text.StaffUnexpected,
    };

    /// <summary>What confirming <paramref name="action"/> will do, said before it is done.</summary>
    public static string ConsequenceOf(ServiceAction action) => action switch
    {
        ServiceAction.StartMaintenance => Text.ConfirmMaintenanceHint,
        ServiceAction.EmergencyStop => Text.ConfirmEmergencyStopHint,
        _ => Text.ConfirmReturnHint,
    };

    /// <summary>
    /// The authenticator URI as a PNG data URI, drawn here in the browser so the key never travels to any other
    /// service. The content security policy already allows <c>data:</c> images.
    /// </summary>
    public static string QrCodeDataUri(string authenticatorUri)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(authenticatorUri, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(5);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }
}
