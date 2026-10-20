using System.Security.Claims;
using ElevatorSystem.Api.Hosting;
using ElevatorSystem.Application.Audit;
using ElevatorSystem.Infrastructure.Staff;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ElevatorSystem.Api.Staff;

/// <summary>
/// Staff accounts, sign-in and the audit trail (ADR-0022, ADR-0023): ASP.NET Core Identity over the staff
/// database, a hardened session cookie, antiforgery, the authorization policies and the Data Protection key ring.
/// </summary>
internal static class StaffHostingExtensions
{
    public const string ApplicationName = "ElevatorSystem";
    public const string SessionCookieName = "__Host-elevator-session";
    public const string TwoFactorCookieName = "__Host-elevator-2fa";
    public const string AntiforgeryCookieName = "__Host-elevator-antiforgery";
    public const string AntiforgeryHeaderName = "X-CSRF-TOKEN";

    /// <summary>A session ends after this long without a request (threat T-API-02).</summary>
    public static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromMinutes(20);

    /// <summary>How soon a password change, disabled account or new role reaches existing sessions.</summary>
    public static readonly TimeSpan SecurityStampValidationInterval = TimeSpan.FromMinutes(1);

    public static IHostApplicationBuilder AddStaff(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;
        var configuration = builder.Configuration;
        var hosting = configuration.GetSection(HostingOptions.SectionName).Get<HostingOptions>() ?? new HostingOptions();

        services.AddOptions<StaffOptions>().BindConfiguration(StaffOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        if (hosting.Mode == HostingMode.SingleProcess)
        {
            // Nothing survives a restart in this mode (ADR-0021), staff accounts and the audit trail included.
            services.AddSingleton<SingleProcessStaffDatabase>();
            services.AddStaffStore((provider, options) =>
                options.UseSqlite(provider.GetRequiredService<SingleProcessStaffDatabase>().ConnectionString));
        }
        else
        {
            services.AddStaffStore((provider, options) =>
            {
                var name = provider.GetRequiredService<IOptions<StaffOptions>>().Value.ConnectionStringName;
                var connectionString = provider.GetRequiredService<IConfiguration>().GetConnectionString(name);
                StaffDbContext.ConfigureNpgsql(
                    options,
                    string.IsNullOrWhiteSpace(connectionString)
                        ? throw new InvalidOperationException($"The connection string 'ConnectionStrings:{name}' for the staff database is missing.")
                        : connectionString);
            });
        }

        services.AddSingleton<StaffStartupService>();
        services.AddHostedService(provider => provider.GetRequiredService<StaffStartupService>());
        services.AddHealthChecks().AddCheck<StaffDatabaseHealthCheck>(StaffDatabaseHealthCheck.Name, tags: ["ready"]);
        AddAuditTrail(services);
        AddIdentity(services);
        AddCookies(services);

        services.AddAntiforgery(options =>
        {
            options.HeaderName = AntiforgeryHeaderName;
            options.Cookie.Name = AntiforgeryCookieName;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        });
        services.AddAuthorization(StaffPolicies.Configure);

        // Keys protect session cookies, antiforgery tokens and the stored authenticator keys, so they are kept in
        // the staff database: a restart or a second API instance can still read them (threat T-DB-05, OQ-13).
        services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .PersistKeysToDbContext<StaffDbContext>();

        services.AddSingleton<ServiceActionIdempotency>();
        return builder;
    }

    private static void AddAuditTrail(IServiceCollection services)
    {
        services.AddOptions<AuditOptions>().BindConfiguration(AuditOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AuditOptions>, AuditOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ClientPseudonymizer>();
        services.AddSingleton<AuditTrail>();
    }

    private static void AddIdentity(IServiceCollection services)
    {
        services.AddIdentityCore<StaffUser>(options =>
            {
                // Length over composition rules (NIST SP 800-63B): long passphrases, no forced symbol soup.
                options.Password.RequiredLength = 12;
                options.Password.RequiredUniqueChars = 4;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;

                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.AllowedForNewUsers = true;

                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedAccount = false;
                options.Tokens.AuthenticatorIssuer = ApplicationName;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddUserStore<ProtectedStaffUserStore>()
            .AddRoleStore<RoleStore<IdentityRole<Guid>, StaffDbContext, Guid>>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        services.TryAddScoped<ISecurityStampValidator, SecurityStampValidator<StaffUser>>();
        services.TryAddScoped<ITwoFactorSecurityStampValidator, TwoFactorSecurityStampValidator<StaffUser>>();
        services.Configure<SecurityStampValidatorOptions>(options =>
        {
            options.ValidationInterval = SecurityStampValidationInterval;
            // A refreshed cookie must still say how the session was signed in: with two factors, or as the test account.
            options.OnRefreshingPrincipal = context =>
            {
                if (context.NewPrincipal?.Identity is not ClaimsIdentity identity || context.CurrentPrincipal is not { } current)
                {
                    return Task.CompletedTask;
                }

                if (current.SignedInWithTwoFactors() && !context.NewPrincipal.SignedInWithTwoFactors())
                {
                    identity.AddClaim(new Claim(StaffPolicies.AuthenticationMethodClaim, StaffPolicies.MultiFactorMethod));
                }

                if (current.SignedInAsTestAccount() && !context.NewPrincipal.SignedInAsTestAccount())
                {
                    identity.AddClaim(new Claim(StaffPolicies.AuthenticationMethodClaim, StaffPolicies.TestAccountMethod));
                }

                return Task.CompletedTask;
            };
        });
    }

    private static void AddCookies(IServiceCollection services)
    {
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();

        services.ConfigureApplicationCookie(options =>
        {
            Harden(options, SessionCookieName);
            options.ExpireTimeSpan = SessionIdleTimeout;
            options.SlidingExpiration = true;

            // An API answers 401 and 403; the browser UI decides where to go, so there are no redirects.
            options.Events.OnRedirectToLogin = context => Status(context, StatusCodes.Status401Unauthorized);
            options.Events.OnRedirectToAccessDenied = context => Status(context, StatusCodes.Status403Forbidden);
            options.Events.OnRedirectToLogout = context => Status(context, StatusCodes.Status204NoContent);
            options.Events.OnRedirectToReturnUrl = context => Status(context, StatusCodes.Status204NoContent);
        });

        services.Configure<CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme, options => Harden(options, TwoFactorCookieName));
        services.Configure<CookieAuthenticationOptions>(IdentityConstants.TwoFactorRememberMeScheme, options => Harden(options, "__Host-elevator-2fa-remember"));
        services.Configure<CookieAuthenticationOptions>(IdentityConstants.ExternalScheme, options => Harden(options, "__Host-elevator-external"));
    }

    /// <summary>
    /// <c>__Host-</c> prefix (HTTPS only, this host only, whole site), not readable from scripts, and never sent
    /// on a request started by another site.
    /// </summary>
    private static void Harden(CookieAuthenticationOptions options, string name)
    {
        options.Cookie.Name = name;
        options.Cookie.Path = "/";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
    }

    private static Task Status<TOptions>(Microsoft.AspNetCore.Authentication.RedirectContext<TOptions> context, int status)
        where TOptions : Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions
    {
        context.Response.StatusCode = status;
        return Task.CompletedTask;
    }
}

/// <summary>
/// The single-process staff database: a SQLite database that lives in memory for as long as this object holds
/// its first connection open. Each host gets its own, so parallel test hosts never share accounts.
/// </summary>
internal sealed class SingleProcessStaffDatabase : IDisposable
{
    private readonly SqliteConnection _keepAlive;

    public SingleProcessStaffDatabase()
    {
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = "staff-" + Guid.NewGuid().ToString("N"),
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        _keepAlive = new SqliteConnection(ConnectionString);
        _keepAlive.Open();
    }

    public string ConnectionString { get; }

    public void Dispose() => _keepAlive.Dispose();
}
