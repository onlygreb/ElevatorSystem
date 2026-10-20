using System.Globalization;
using System.Security.Claims;
using System.Text;
using ElevatorSystem.Api.Security;
using ElevatorSystem.Api.Staff;
using ElevatorSystem.Application.Audit;
using ElevatorSystem.Contracts;
using ElevatorSystem.Infrastructure.Staff;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;

namespace ElevatorSystem.Api.Endpoints;

/// <summary>
/// Staff sign-in, sign-out, the current session and two-factor registration (ADR-0022). There is no public
/// registration: accounts are created from configuration. Every <c>POST</c> needs an antiforgery token.
/// </summary>
internal static class StaffAuthEndpoints
{
    public const string Tag = "Staff";
    public const int MaxEmailLength = 256;
    public const int MaxPasswordLength = 128;
    public const int MaxCodeLength = 32;
    public const int RecoveryCodeCount = 10;

    public static IEndpointRouteBuilder MapStaffAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup(string.Empty).WithTags(Tag);

        auth.MapGet(ApiRoutes.Antiforgery, GetAntiforgeryToken)
            .AllowAnonymous()
            .WithName(nameof(GetAntiforgeryToken))
            .WithSummary("Get an antiforgery token")
            .WithDescription("Send the token in the named header with every staff POST. Fetch a new one after signing in or out.")
            .RequireRateLimiting(RateLimiting.ReadPolicy)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost(ApiRoutes.SignIn, SignInAsync)
            .AllowAnonymous()
            .WithName("SignIn")
            .WithSummary("Sign a staff member in")
            .WithDescription("Sets the session cookie. Accounts with two-factor authentication send a code too; without it the answer is 401 with reason TwoFactorRequired. Five failures lock the account for 15 minutes.")
            .RequireRateLimiting(RateLimiting.SignInPolicy)
            .WithRequestSizeLimit()
            .RequireAntiforgeryToken()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost(ApiRoutes.SignOut, SignOutAsync)
            .RequireAuthorization()
            .WithName("SignOut")
            .WithSummary("Sign out")
            .RequireRateLimiting(RateLimiting.StaffPolicy)
            .WithRequestSizeLimit()
            .RequireAntiforgeryToken()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapGet(ApiRoutes.Session, GetSessionAsync)
            .RequireAuthorization()
            .WithName("GetSession")
            .WithSummary("Get the signed-in staff member")
            .RequireRateLimiting(RateLimiting.StaffPolicy)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost(ApiRoutes.TwoFactorSetup, SetUpTwoFactorAsync)
            .RequireAuthorization()
            .WithName("SetUpTwoFactor")
            .WithSummary("Get a new authenticator key")
            .WithDescription("Only while two-factor authentication is not enabled; each call replaces the previous key.")
            .RequireRateLimiting(RateLimiting.StaffPolicy)
            .WithRequestSizeLimit()
            .RequireAntiforgeryToken()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost(ApiRoutes.TwoFactorEnable, EnableTwoFactorAsync)
            .RequireAuthorization()
            .WithName("EnableTwoFactor")
            .WithSummary("Enable two-factor authentication")
            .WithDescription("Checks a code from the authenticator app, enables two-factor authentication, marks this session as signed in with two factors and returns one-time recovery codes.")
            .RequireRateLimiting(RateLimiting.StaffPolicy)
            .WithRequestSizeLimit()
            .RequireAntiforgeryToken()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return endpoints;
    }

    internal static Ok<AntiforgeryTokenDto> GetAntiforgeryToken(IAntiforgery antiforgery, HttpContext context)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        // The token is tied to the caller and must not be cached by anything between the browser and the API.
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new AntiforgeryTokenDto(tokens.HeaderName!, tokens.RequestToken!));
    }

    internal static async Task<Results<Ok<StaffSessionDto>, ProblemHttpResult>> SignInAsync(
        SignInRequest request,
        SignInManager<StaffUser> signInManager,
        UserManager<StaffUser> users,
        AuditTrail audit,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (request.Email.Length > MaxEmailLength || request.Password.Length > MaxPasswordLength
            || request.TwoFactorCode?.Length > MaxCodeLength || request.RecoveryCode?.Length > MaxCodeLength)
        {
            return SignInFailed(SignInFailureReason.InvalidCredentials);
        }

        var user = await users.FindByEmailAsync(request.Email);
        if (user is null)
        {
            // Spend the time a real password check takes, so response times do not reveal which e-mails exist.
            users.PasswordHasher.VerifyHashedPassword(new StaffUser(), UnknownAccountHash.Value, request.Password);
            await RecordSignInAsync(audit, AuditOutcomes.Failed, null, context, cancellationToken);
            return SignInFailed(SignInFailureReason.InvalidCredentials);
        }

        var result = await signInManager.PasswordSignInAsync(user, request.Password, isPersistent: false, lockoutOnFailure: true);
        var usedSecondFactor = false;
        if (result.RequiresTwoFactor)
        {
            if (!string.IsNullOrWhiteSpace(request.TwoFactorCode))
            {
                result = await signInManager.TwoFactorAuthenticatorSignInAsync(NormaliseCode(request.TwoFactorCode), isPersistent: false, rememberClient: false);
            }
            else if (!string.IsNullOrWhiteSpace(request.RecoveryCode))
            {
                result = await signInManager.TwoFactorRecoveryCodeSignInAsync(NormaliseRecoveryCode(request.RecoveryCode));
            }
            else
            {
                return SignInFailed(SignInFailureReason.TwoFactorRequired);
            }

            usedSecondFactor = true;
        }

        if (result.Succeeded)
        {
            await RecordSignInAsync(audit, AuditOutcomes.Succeeded, user.Id, context, cancellationToken);
            return TypedResults.Ok(await SessionAsync(users, user, usedSecondFactor));
        }

        await RecordSignInAsync(audit, result.IsLockedOut ? AuditOutcomes.LockedOut : AuditOutcomes.Failed, user.Id, context, cancellationToken);
        return SignInFailed(usedSecondFactor && !result.IsLockedOut ? SignInFailureReason.InvalidTwoFactorCode : SignInFailureReason.InvalidCredentials);
    }

    internal static async Task<NoContent> SignOutAsync(
        SignInManager<StaffUser> signInManager, UserManager<StaffUser> users, AuditTrail audit, HttpContext context, CancellationToken cancellationToken)
    {
        var accountId = AccountId(users, context.User);
        await signInManager.SignOutAsync();
        await audit.RecordAsync(
            new AuditRecord(AuditActions.SignOut, string.Empty, AuditOutcomes.Succeeded, accountId, context.Connection.RemoteIpAddress),
            cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<Results<Ok<StaffSessionDto>, UnauthorizedHttpResult>> GetSessionAsync(UserManager<StaffUser> users, HttpContext context)
    {
        var user = await users.GetUserAsync(context.User);
        return user is null
            ? TypedResults.Unauthorized()
            : TypedResults.Ok(await SessionAsync(users, user, context.User.SignedInWithTwoFactors()));
    }

    internal static async Task<Results<Ok<TwoFactorSetupDto>, ProblemHttpResult, UnauthorizedHttpResult>> SetUpTwoFactorAsync(
        UserManager<StaffUser> users, SignInManager<StaffUser> signInManager, HttpContext context)
    {
        var user = await users.GetUserAsync(context.User);
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        if (user.TwoFactorEnabled)
        {
            return AlreadyEnabled();
        }

        await users.ResetAuthenticatorKeyAsync(user);
        // A new key changes the security stamp; renew the cookie so this session stays valid.
        await signInManager.RefreshSignInAsync(user);
        var key = (await users.GetAuthenticatorKeyAsync(user))!;
        return TypedResults.Ok(new TwoFactorSetupDto(FormatKey(key), AuthenticatorUri(user.Email!, key)));
    }

    internal static async Task<Results<Ok<RecoveryCodesDto>, ProblemHttpResult, UnauthorizedHttpResult>> EnableTwoFactorAsync(
        EnableTwoFactorRequest request,
        UserManager<StaffUser> users,
        SignInManager<StaffUser> signInManager,
        AuditTrail audit,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var user = await users.GetUserAsync(context.User);
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        if (user.TwoFactorEnabled)
        {
            return AlreadyEnabled();
        }

        if (request.Code.Length > MaxCodeLength
            || !await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, NormaliseCode(request.Code)))
        {
            // Wrong codes count towards the lockout, like wrong passwords.
            await users.AccessFailedAsync(user);
            return TypedResults.Problem(
                detail: "The code does not match the authenticator key. Check the app and the device's clock, then try again.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid code",
                extensions: new Dictionary<string, object?> { [ApiRoutes.RejectionReasonExtension] = nameof(SignInFailureReason.InvalidTwoFactorCode) });
        }

        await users.SetTwoFactorEnabledAsync(user, true);
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount);
        // The code just proved the second factor, so this session counts as signed in with it.
        await signInManager.SignInWithClaimsAsync(user, isPersistent: false, [new Claim(StaffPolicies.AuthenticationMethodClaim, StaffPolicies.MultiFactorMethod)]);
        await audit.RecordAsync(
            new AuditRecord(AuditActions.TwoFactorEnabled, string.Empty, AuditOutcomes.Succeeded, user.Id, context.Connection.RemoteIpAddress),
            cancellationToken);
        return TypedResults.Ok(new RecoveryCodesDto([.. codes ?? []]));
    }

    internal static Guid? AccountId(UserManager<StaffUser> users, ClaimsPrincipal principal) =>
        Guid.TryParse(users.GetUserId(principal), out var id) ? id : null;

    private static readonly Lazy<string> UnknownAccountHash = new(() =>
        new PasswordHasher<StaffUser>().HashPassword(new StaffUser(), Guid.NewGuid().ToString("N")));

    private static async Task<StaffSessionDto> SessionAsync(UserManager<StaffUser> users, StaffUser user, bool twoFactorVerified)
    {
        var roles = (await users.GetRolesAsync(user))
            .Select(role => Enum.TryParse<StaffRole>(role, out var known) ? known : (StaffRole?)null)
            .OfType<StaffRole>()
            .Order()
            .ToList();
        return new StaffSessionDto(user.Email!, roles, user.TwoFactorEnabled, twoFactorVerified);
    }

    private static Task<AuditEntry> RecordSignInAsync(AuditTrail audit, string outcome, Guid? accountId, HttpContext context, CancellationToken cancellationToken)
    {
        return audit.RecordAsync(
            new AuditRecord(AuditActions.SignIn, string.Empty, outcome, accountId, context.Connection.RemoteIpAddress),
            cancellationToken);
    }

    private static ProblemHttpResult SignInFailed(SignInFailureReason reason) => TypedResults.Problem(
        detail: reason switch
        {
            SignInFailureReason.TwoFactorRequired => "Enter the code from your authenticator app, or a recovery code.",
            SignInFailureReason.InvalidTwoFactorCode => "The code is not valid. Codes change every 30 seconds; recovery codes work once.",
            _ => "The e-mail or password is not correct, or the account is locked for a while after too many attempts.",
        },
        statusCode: StatusCodes.Status401Unauthorized,
        title: reason == SignInFailureReason.TwoFactorRequired ? "Second factor required" : "Sign-in failed",
        extensions: new Dictionary<string, object?> { [ApiRoutes.RejectionReasonExtension] = reason.ToString() });

    private static ProblemHttpResult AlreadyEnabled() => TypedResults.Problem(
        detail: "Two-factor authentication is already enabled for this account.",
        statusCode: StatusCodes.Status409Conflict,
        title: "Already enabled");

    /// <summary>Recovery codes are stored as shown (<c>XXXXX-XXXXX</c>), so only spaces and case are forgiven.</summary>
    private static string NormaliseRecoveryCode(string code) => code.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();

    /// <summary>Authenticator codes are typed with spaces or dashes as often as without.</summary>
    private static string NormaliseCode(string code) => code.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);

    /// <summary>Groups of four lower-case characters, the way authenticator apps show keys typed by hand.</summary>
    private static string FormatKey(string key)
    {
        var formatted = new StringBuilder(key.Length + (key.Length / 4));
        for (var index = 0; index < key.Length; index += 4)
        {
            formatted.Append(key.AsSpan(index, Math.Min(4, key.Length - index))).Append(' ');
        }

        return formatted.ToString().TrimEnd().ToLowerInvariant();
    }

    /// <summary>The <c>otpauth://</c> URI (Key Uri Format) that authenticator apps read from a QR code.</summary>
    private static string AuthenticatorUri(string email, string key) => string.Create(
        CultureInfo.InvariantCulture,
        $"otpauth://totp/{Uri.EscapeDataString(StaffHostingExtensions.ApplicationName)}:{Uri.EscapeDataString(email)}?secret={key}&issuer={Uri.EscapeDataString(StaffHostingExtensions.ApplicationName)}&digits=6");
}
