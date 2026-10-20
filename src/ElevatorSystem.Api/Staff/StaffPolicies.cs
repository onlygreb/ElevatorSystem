using System.Security.Claims;
using ElevatorSystem.Contracts;
using Microsoft.AspNetCore.Authorization;

namespace ElevatorSystem.Api.Staff;

/// <summary>
/// Authorization policies (ADR-0022). Every staff capability needs a session signed in with the second factor;
/// an account without one can only see its own session and register an authenticator app. The one exception is the
/// configured test account (ADR-0030), whose sessions carry their own authentication method.
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

    /// <summary>The <c>amr</c> value of a test account's session: signed in with its password only, on purpose (ADR-0030).</summary>
    public const string TestAccountMethod = "test-account";

    /// <summary>The account claim that marks an account created as the test account.</summary>
    public const string TestAccountClaim = "elevator:test-account";

    public static void Configure(AuthorizationOptions options)
    {
        options.AddPolicy(Staff, policy => policy
            .RequireAuthenticatedUser()
            .RequireRole(nameof(StaffRole.Admin), nameof(StaffRole.Auditor))
            .RequireAssertion(context => context.User.MeetsTheSecondFactorRule()));

        options.AddPolicy(ServiceControl, policy => policy
            .RequireAuthenticatedUser()
            .RequireRole(nameof(StaffRole.Admin))
            .RequireAssertion(context => context.User.MeetsTheSecondFactorRule()));
    }

    public static bool SignedInWithTwoFactors(this ClaimsPrincipal user) =>
        user.HasClaim(AuthenticationMethodClaim, MultiFactorMethod);

    public static bool SignedInAsTestAccount(this ClaimsPrincipal user) =>
        user.HasClaim(AuthenticationMethodClaim, TestAccountMethod);

    /// <summary>Signed in with the second factor, or as the configured test account.</summary>
    public static bool MeetsTheSecondFactorRule(this ClaimsPrincipal user) =>
        user.SignedInWithTwoFactors() || user.SignedInAsTestAccount();
}
