namespace ElevatorSystem.Contracts;

/// <summary>HTTP routes of the elevator API, version 1. Shared by the server and the browser client.</summary>
public static class ApiRoutes
{
    public const string Base = "/api/v1";

    /// <summary><c>POST</c> a <see cref="HallCallRequest"/>; answers <c>202</c> with a <see cref="RequestAcceptedResponse"/>.</summary>
    public const string HallCalls = Base + "/hall-calls";

    /// <summary><c>POST</c> a <see cref="CarCallRequest"/>; answers <c>202</c> with a <see cref="RequestAcceptedResponse"/>.</summary>
    public const string CarCalls = Base + "/car-calls";

    /// <summary><c>GET</c> the current <see cref="ElevatorStatusDto"/>.</summary>
    public const string Elevator = Base + "/elevator";

    /// <summary><c>GET</c> the <see cref="ElevatorConfigurationDto"/> (building and timings).</summary>
    public const string Configuration = Base + "/elevator/configuration";

    /// <summary><c>GET</c> the current <see cref="BuildingOccupancyDto"/> (people waiting, riding and present).</summary>
    public const string Occupancy = Base + "/building/occupancy";

    /// <summary><c>GET</c> an <see cref="AntiforgeryTokenDto"/>; fetch a new one after signing in or out.</summary>
    public const string Antiforgery = Base + "/auth/antiforgery";

    /// <summary><c>POST</c> a <see cref="SignInRequest"/>; answers <c>200</c> with a <see cref="StaffSessionDto"/> and sets the session cookie.</summary>
    public const string SignIn = Base + "/auth/sign-in";

    /// <summary><c>POST</c> to end the session.</summary>
    public const string SignOut = Base + "/auth/sign-out";

    /// <summary><c>GET</c> the <see cref="StaffSessionDto"/> of the signed-in staff member, or <c>401</c>.</summary>
    public const string Session = Base + "/auth/session";

    /// <summary><c>POST</c> to get a new authenticator key (<see cref="TwoFactorSetupDto"/>) while two-factor is not enabled.</summary>
    public const string TwoFactorSetup = Base + "/auth/two-factor/setup";

    /// <summary><c>POST</c> an <see cref="EnableTwoFactorRequest"/>; answers <see cref="RecoveryCodesDto"/>.</summary>
    public const string TwoFactorEnable = Base + "/auth/two-factor/enable";

    /// <summary><c>POST</c> a <see cref="ServiceActionRequest"/> (admins); answers <c>202</c> with a <see cref="ServiceActionAcceptedResponse"/>.</summary>
    public const string ServiceActions = Base + "/admin/service-actions";

    /// <summary><c>GET</c> an <see cref="AuditPageDto"/>; query <c>before</c> and <c>limit</c>.</summary>
    public const string AuditEntries = Base + "/admin/audit/entries";

    /// <summary><c>GET</c> an <see cref="AuditVerificationDto"/> for the whole chain.</summary>
    public const string AuditVerification = Base + "/admin/audit/verification";

    /// <summary>Optional header on a service action: a UUID that makes a retry return the first answer instead of acting twice.</summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>
    /// Name of the ProblemDetails extension that carries a <see cref="RejectionReason"/> when a request is refused.
    /// </summary>
    public const string RejectionReasonExtension = "reason";
}

/// <summary>The real-time channel: a SignalR hub at <see cref="Path"/> that calls <see cref="IElevatorClient"/>.</summary>
public static class ElevatorHub
{
    public const string Path = "/hubs/elevator";
}
