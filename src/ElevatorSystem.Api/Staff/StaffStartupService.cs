using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using ElevatorSystem.Api.Hosting;
using ElevatorSystem.Application.Audit;
using ElevatorSystem.Contracts;
using ElevatorSystem.Infrastructure.Staff;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace ElevatorSystem.Api.Staff;

/// <summary>
/// Prepares the staff database: brings the schema up to date (retrying while the database is unreachable), creates
/// the roles and creates the configured accounts that do not exist yet. In single-process mode the in-memory database
/// is ready before the host finishes starting; in distributed mode this runs in the background, like the engine's
/// migrations, and <see cref="StaffDatabaseHealthCheck"/> keeps the API not ready until it is done.
/// </summary>
internal sealed class StaffStartupService(
    StaffDatabaseInitializer initializer,
    IServiceScopeFactory scopes,
    IOptions<StaffOptions> options,
    IOptions<HostingOptions> hosting,
    ClientPseudonymizer pseudonymizer,
    TimeProvider timeProvider,
    IHostApplicationLifetime lifetime,
    ILogger<StaffStartupService> logger) : IHostedService
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    private Task _preparation = Task.CompletedTask;

    /// <summary>The staff database is migrated and seeded.</summary>
    public bool IsReady => _preparation.IsCompletedSuccessfully;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (hosting.Value.Mode == HostingMode.SingleProcess)
        {
            _preparation = PrepareAsync(cancellationToken);
            await _preparation;
            return;
        }

        var stopping = lifetime.ApplicationStopping;
        _preparation = Task.Run(
            async () =>
            {
                try
                {
                    await PrepareAsync(stopping);
                }
                // A configuration error (say, a seeded password the policy refuses) leaves the API not ready.
                catch (Exception exception) when (!stopping.IsCancellationRequested)
                {
                    ApiLog.StaffPreparationFailed(logger, exception);
                    throw;
                }
            },
            CancellationToken.None);

        // A head start: hosted services registered after this one (data protection reads its key ring at start-up) then
        // find the schema in place, rather than logging errors on a fresh database. A preparation still waiting for the
        // database goes on in the background, as before.
        await Task.WhenAny(_preparation, Task.Delay(options.Value.DatabaseHeadStart, timeProvider, cancellationToken));
    }

    /// <summary>Waits for a preparation still running; the host's stopping token has already cancelled it.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _preparation.WaitAsync(cancellationToken);
        }
        // Stopping before it finished, or after it failed (already logged): nothing more to do.
        catch (Exception) when (_preparation.IsCompleted || cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task PrepareAsync(CancellationToken cancellationToken)
    {
        await InitializeDatabaseAsync(cancellationToken);

        await using var scope = scopes.CreateAsyncScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var role in Enum.GetNames<StaffRole>())
        {
            if (!await roles.RoleExistsAsync(role))
            {
                Require(await roles.CreateAsync(new IdentityRole<Guid>(role)), $"create the {role} role");
            }
        }

        var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        for (var index = 0; index < options.Value.Accounts.Count; index++)
        {
            var seed = options.Value.Accounts[index];
            // Docker Compose passes the first admin even when no password is set (ADR-0034): then there is nobody to create.
            if (string.IsNullOrEmpty(seed.Password))
            {
                ApiLog.StaffAccountSkipped(logger, index);
                continue;
            }

            await SeedAsync(users, seed);
        }

        if (options.Value.ConfiguredTestAccount is { } testAccount)
        {
            await SeedAsync(users, testAccount, isTestAccount: true);
            ApiLog.TestAccountEnabled(logger);
        }

        if (pseudonymizer.IsEphemeral)
        {
            ApiLog.EphemeralPseudonymKey(logger);
        }
    }

    private async Task InitializeDatabaseAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await initializer.InitializeAsync(cancellationToken);
                ApiLog.StaffDatabaseReady(logger);
                return;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                ApiLog.StaffDatabaseUnavailable(logger, exception, RetryInterval);
            }

            await Task.Delay(RetryInterval, timeProvider, cancellationToken);
        }
    }

    private async Task SeedAsync(UserManager<StaffUser> users, StaffAccountSeed seed, bool isTestAccount = false)
    {
        var problems = new List<ValidationResult>();
        if (!Validator.TryValidateObject(seed, new ValidationContext(seed), problems, validateAllProperties: true))
        {
            throw new InvalidOperationException("A configured staff account is invalid: " + string.Join("; ", problems.Select(problem => problem.ErrorMessage)));
        }

        if (await users.FindByEmailAsync(seed.Email) is not null)
        {
            return;
        }

        var user = new StaffUser { UserName = seed.Email, Email = seed.Email, EmailConfirmed = true, LockoutEnabled = true };
        Require(await users.CreateAsync(user, seed.Password), "create a configured staff account");
        Require(await users.AddToRoleAsync(user, seed.Role.ToString()), "give a configured staff account its role");
        if (isTestAccount)
        {
            // Only an account created as the test account can sign in without a second factor (ADR-0030).
            Require(await users.AddClaimAsync(user, new Claim(StaffPolicies.TestAccountClaim, "true")), "mark the test account");
        }

        ApiLog.StaffAccountCreated(logger, seed.Role);
    }

    /// <summary>Fails start-up with Identity's error codes only: never the e-mail or the password.</summary>
    private static void Require(IdentityResult result, string what)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Could not {what}: {string.Join(", ", result.Errors.Select(error => error.Code))}.");
        }
    }
}

/// <summary>Readiness: staff sign-in and the audit trail need the staff database migrated and seeded.</summary>
internal sealed class StaffDatabaseHealthCheck(StaffStartupService startup) : IHealthCheck
{
    public const string Name = "staff-database";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(startup.IsReady
            ? HealthCheckResult.Healthy("The staff database is ready.")
            : new HealthCheckResult(context.Registration.FailureStatus, "The staff database is not ready yet."));
}
