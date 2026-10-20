using System.Net;
using System.Net.Http.Json;
using ElevatorSystem.Contracts;
using ElevatorSystem.Infrastructure.Staff;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ElevatorSystem.Api.Tests.Staff;

/// <summary>Sign-in, two-factor registration, sessions and their cookies (ADR-0022).</summary>
public sealed class StaffSignInTests
{
    [Fact]
    public async Task SignIn_WithThePasswordOfAnAccountWithoutTwoFactor_StartsAPasswordOnlySession()
    {
        await using var factory = new ApiFactory();
        using var client = StaffClient.Anonymous(factory);

        using var response = await client.SignInAsync(StaffAccounts.Admin.Email, StaffAccounts.Admin.Password);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var session = await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.StaffSessionDto, Http.Cancellation);
        session.ShouldBe(new StaffSessionDto(StaffAccounts.Admin.Email, session!.Roles, TwoFactorEnabled: false, TwoFactorVerified: false));
        session.Roles.ShouldBe([StaffRole.Admin]);
        using var current = await client.GetAsync(ApiRoutes.Session);
        current.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SignIn_UnknownEmailAndWrongPassword_AreRefusedTheSameWay()
    {
        await using var factory = new ApiFactory();
        using var client = StaffClient.Anonymous(factory);

        using var unknown = await client.SignInAsync("nobody@elevator.test", StaffAccounts.Admin.Password);
        using var wrong = await client.SignInAsync(StaffAccounts.Admin.Email, "not the password at all");

        var unknownProblem = await unknown.ShouldBeProblemAsync(HttpStatusCode.Unauthorized);
        var wrongProblem = await wrong.ShouldBeProblemAsync(HttpStatusCode.Unauthorized);
        unknownProblem.Reason().ShouldBe(nameof(SignInFailureReason.InvalidCredentials));
        wrongProblem.Reason().ShouldBe(nameof(SignInFailureReason.InvalidCredentials));
        unknownProblem.GetProperty("detail").GetString().ShouldBe(wrongProblem.GetProperty("detail").GetString(), "which e-mails exist must not show");
    }

    [Fact]
    public async Task SignIn_AfterFiveWrongPasswords_IsRefusedEvenWithTheRightOne()
    {
        await using var factory = new ApiFactory();
        using var client = StaffClient.Anonymous(factory);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var wrong = await client.SignInAsync(StaffAccounts.Admin.Email, "a wrong password " + attempt);
            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var right = await client.SignInAsync(StaffAccounts.Admin.Email, StaffAccounts.Admin.Password);

        (await right.ShouldBeProblemAsync(HttpStatusCode.Unauthorized)).Reason().ShouldBe(nameof(SignInFailureReason.InvalidCredentials));
        using var auditor = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Auditor);
        var outcomes = (await AuditTrailEndpointTests.EntriesAsync(auditor))
            .Where(entry => entry.Action == "staff.sign-in" && entry.Actor == StaffAccounts.Admin.Email)
            .Select(entry => entry.Outcome)
            .ToList();
        outcomes.ShouldBe(["locked-out", "locked-out", "failed", "failed", "failed", "failed"], "the fifth failure locks the account; the right password then finds it locked");
    }

    [Fact]
    public async Task SignIn_WithoutTheAntiforgeryToken_IsRefused()
    {
        await using var factory = new ApiFactory();
        using var client = StaffClient.Anonymous(factory);

        using var response = await client.PostAsync(
            ApiRoutes.SignIn, new SignInRequest(StaffAccounts.Admin.Email, StaffAccounts.Admin.Password), antiforgery: false);

        (await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest)).Reason().ShouldBe("Antiforgery");
        response.Headers.Contains("Set-Cookie").ShouldBeFalse("no session starts without the token");
    }

    [Fact]
    public async Task SignIn_SetsAHardenedSessionCookie()
    {
        await using var factory = new ApiFactory();
        using var client = StaffClient.Anonymous(factory);

        using var response = await client.SignInAsync(StaffAccounts.Admin.Email, StaffAccounts.Admin.Password);

        var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("__Host-elevator-session=", StringComparison.Ordinal));
        cookie.ShouldContain("secure", Case.Insensitive);
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("samesite=strict", Case.Insensitive);
        cookie.ShouldContain("path=/", Case.Insensitive);
        cookie.ShouldNotContain("domain=", Case.Insensitive);
        cookie.ShouldNotContain("expires=", Case.Insensitive, "a session cookie ends with the browser session");
    }

    [Fact]
    public async Task SignIn_WithTwoFactorEnabled_NeedsAValidCode()
    {
        await using var factory = new ApiFactory();
        string key;
        using (var enrolled = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Admin))
        {
            key = enrolled.AuthenticatorKey!;
        }

        using var client = StaffClient.Anonymous(factory);
        using var withoutCode = await client.SignInAsync(StaffAccounts.Admin.Email, StaffAccounts.Admin.Password);
        using var wrongCode = await client.SignInAsync(StaffAccounts.Admin.Email, StaffAccounts.Admin.Password, WrongCode(key));
        using var rightCode = await client.SignInAsync(StaffAccounts.Admin.Email, StaffAccounts.Admin.Password, Totp.Code(key));

        (await withoutCode.ShouldBeProblemAsync(HttpStatusCode.Unauthorized)).Reason().ShouldBe(nameof(SignInFailureReason.TwoFactorRequired));
        (await wrongCode.ShouldBeProblemAsync(HttpStatusCode.Unauthorized)).Reason().ShouldBe(nameof(SignInFailureReason.InvalidTwoFactorCode));
        rightCode.StatusCode.ShouldBe(HttpStatusCode.OK);
        var session = await rightCode.Content.ReadFromJsonAsync(ContractsJsonContext.Default.StaffSessionDto, Http.Cancellation);
        session.ShouldNotBeNull().TwoFactorVerified.ShouldBeTrue();
        session.TwoFactorEnabled.ShouldBeTrue();
    }

    [Fact]
    public async Task SignIn_WithARecoveryCode_WorksOnce()
    {
        await using var factory = new ApiFactory();
        IReadOnlyList<string> codes;
        using (var enrolled = await StaffClient.SignedInWithPasswordAsync(factory, StaffAccounts.Auditor))
        {
            codes = await enrolled.EnableTwoFactorAsync();
        }

        codes.Count.ShouldBe(10);
        using var client = StaffClient.Anonymous(factory);
        using var first = await client.SignInAsync(StaffAccounts.Auditor.Email, StaffAccounts.Auditor.Password, recoveryCode: codes[0]);
        using var other = StaffClient.Anonymous(factory);
        using var again = await other.SignInAsync(StaffAccounts.Auditor.Email, StaffAccounts.Auditor.Password, recoveryCode: codes[0]);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await again.ShouldBeProblemAsync(HttpStatusCode.Unauthorized)).Reason().ShouldBe(nameof(SignInFailureReason.InvalidTwoFactorCode));
    }

    [Fact]
    public async Task SignOut_EndsTheSession()
    {
        await using var factory = new ApiFactory();
        using var client = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Admin);

        using var signedOut = await client.PostAsync(ApiRoutes.SignOut);
        using var session = await client.GetAsync(ApiRoutes.Session);

        signedOut.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        session.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TwoFactorSetup_OnceEnabled_IsRefused()
    {
        await using var factory = new ApiFactory();
        using var client = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Admin);

        using var response = await client.PostAsync(ApiRoutes.TwoFactorSetup);

        await response.ShouldBeProblemAsync(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task TwoFactorEnable_WithAWrongCode_IsRefusedAndLeavesItDisabled()
    {
        await using var factory = new ApiFactory();
        using var client = await StaffClient.SignedInWithPasswordAsync(factory, StaffAccounts.Admin);
        using var setup = await client.PostAsync(ApiRoutes.TwoFactorSetup);
        var key = (await setup.Content.ReadFromJsonAsync(ContractsJsonContext.Default.TwoFactorSetupDto, Http.Cancellation))!;

        using var response = await client.PostAsync(ApiRoutes.TwoFactorEnable, new EnableTwoFactorRequest(WrongCode(key.SharedKey)));

        (await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest)).Reason().ShouldBe(nameof(SignInFailureReason.InvalidTwoFactorCode));
        using var session = await client.GetAsync(ApiRoutes.Session);
        (await session.Content.ReadFromJsonAsync(ContractsJsonContext.Default.StaffSessionDto, Http.Cancellation))!.TwoFactorEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task TwoFactorSetup_ReturnsAKeyAndAnAuthenticatorUri()
    {
        await using var factory = new ApiFactory();
        using var client = await StaffClient.SignedInWithPasswordAsync(factory, StaffAccounts.Admin);

        using var response = await client.PostAsync(ApiRoutes.TwoFactorSetup);

        var setup = (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.TwoFactorSetupDto, Http.Cancellation))!;
        setup.SharedKey.Replace(" ", "", StringComparison.Ordinal).Length.ShouldBeGreaterThanOrEqualTo(26, "at least 128 bits of key");
        setup.AuthenticatorUri.ShouldStartWith("otpauth://totp/ElevatorSystem:admin%40elevator.test?secret=");
        setup.AuthenticatorUri.ShouldContain("&issuer=ElevatorSystem");
    }

    [Fact]
    public async Task AuthenticatorKey_IsStoredEncrypted()
    {
        await using var factory = new ApiFactory();
        using var client = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Admin);
        var key = client.AuthenticatorKey!.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<StaffDbContext>();
        var stored = await database.UserTokens.AsNoTracking().Select(token => token.Value).ToListAsync(Http.Cancellation);

        stored.Count.ShouldBe(2, "the authenticator key and the recovery codes");
        stored.ShouldAllBe(value => value != null && !value.Contains(key, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SignIns_AreAudited_WithoutTheEmailOrPasswordTried()
    {
        await using var factory = new ApiFactory();
        using var anonymous = StaffClient.Anonymous(factory);
        anonymous.Http.DefaultRequestHeaders.Add(ApiFactory.ClientAddressHeader, "198.51.100.23");
        using var failed = await anonymous.SignInAsync("intruder@elevator.test", "guessing-password-123");
        using var auditor = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Auditor);

        var entries = await AuditTrailEndpointTests.EntriesAsync(auditor);

        var unknown = entries.First(entry => entry.Action == "staff.sign-in" && entry.Outcome == "failed");
        unknown.Actor.ShouldBeNull("nobody is identified by a wrong e-mail");
        unknown.ClientPseudonym.ShouldNotBeNull();
        entries.ShouldNotContain(entry => entry.Target.Contains("intruder", StringComparison.Ordinal) || entry.Target.Contains("guessing", StringComparison.Ordinal));
        entries.ShouldContain(entry => entry.Action == "staff.sign-in" && entry.Outcome == "succeeded" && entry.Actor == StaffAccounts.Auditor.Email);
        entries.ShouldContain(entry => entry.Action == "staff.two-factor-enabled" && entry.Actor == StaffAccounts.Auditor.Email);
    }

    /// <summary>A six-digit code that is not valid in any time step the server accepts.</summary>
    private static string WrongCode(string key)
    {
        var now = DateTimeOffset.UtcNow;
        var valid = Enumerable.Range(-3, 7).Select(step => Totp.Code(key, now.AddSeconds(30 * step))).ToHashSet();
        return Enumerable.Range(0, 1_000_000).Select(code => code.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)).First(code => !valid.Contains(code));
    }
}
