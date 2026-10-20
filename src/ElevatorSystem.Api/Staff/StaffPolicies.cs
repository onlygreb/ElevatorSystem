using System.Security.Claims;
using ElevatorSystem.Contracts;
using Microsoft.AspNetCore.Authorization;

namespace ElevatorSystem.Api.Staff;

/// <summary>
/// Authorization policies (ADR-0022). Every staff capability needs a session signed in with the second factor;
/// an account without one can only see its own session and register an authenticator app.
/// </summary>
internal static class StaffPolicies
{
    /// <summary>Admins and auditors, signed in with two factors: occupancy, the audit trail, the hub's staff group.</summary>
    public const string Staff = "staff";

    /// <summary>Admins signed in with two factors: start maintenance, emergency stop, return to service.</summary>
    public const string ServiceControl = "service-control";

    /// <summary>The claim Identity adds when a sign-in used the second factor (RFC 8176 <c>amr</c>).</summary>
    public const string AuthenticationMethodClaim = "amr";

    public const string MultiFactorMethod = "mfa";

    public static void Configure(AuthorizationOptions options)
    {
        options.AddPolicy(Staff, policy => policy
            .RequireAuthenticatedUser()
            .RequireRole(nameof(StaffRole.Admin), nameof(StaffRole.Auditor))
            .RequireClaim(AuthenticationMethodClaim, MultiFactorMethod));

        options.AddPolicy(ServiceControl, policy => policy
            .RequireAuthenticatedUser()
            .RequireRole(nameof(StaffRole.Admin))
            .RequireClaim(AuthenticationMethodClaim, MultiFactorMethod));
    }

    public static bool SignedInWithTwoFactors(this ClaimsPrincipal user) =>
        user.HasClaim(AuthenticationMethodClaim, MultiFactorMethod);
}
