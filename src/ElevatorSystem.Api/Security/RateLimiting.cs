using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Threading.RateLimiting;
using ElevatorSystem.Api.Endpoints;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace ElevatorSystem.Api.Security;

/// <summary>
/// Abuse protection (threats T-API-01 and T-API-12, abuse case AC-1): a token bucket per client
/// address (per account for signed-in staff) for each kind of endpoint, plus a global cap on concurrent API requests. A refusal is a
/// <c>429</c> problem with <c>Retry-After</c>.
/// </summary>
/// <remarks>
/// The client address is used only as an in-memory partition key and is never stored or logged
/// (ADR-0024). IPv6 clients are grouped by their /64 prefix, which is what one subscriber is usually
/// given, so rotating addresses inside it does not buy extra requests.
/// </remarks>
internal static class RateLimiting
{
    /// <summary>Hall and car calls.</summary>
    public const string PassengerPolicy = "passenger";

    /// <summary>Status and configuration reads.</summary>
    public const string ReadPolicy = "read";

    /// <summary>Staff sign-in attempts, per client address, on top of the account lockout (threat T-API-01).</summary>
    public const string SignInPolicy = "sign-in";

    /// <summary>Staff requests, per signed-in account (per client address before signing in).</summary>
    public const string StaffPolicy = "staff";

    /// <summary>The fallback when a limiter cannot say when to come back.</summary>
    public const int DefaultRetryAfterSeconds = 1;

    public static IHostApplicationBuilder AddApiRateLimiting(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOptions<ApiRateLimitOptions>()
            .BindConfiguration(ApiRateLimitOptions.Section)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddRateLimiter(_ => { });
        builder.Services.AddOptions<RateLimiterOptions>()
            .Configure<IOptions<ApiRateLimitOptions>>((limiter, settings) => Configure(limiter, settings.Value));

        return builder;
    }

    /// <summary>The key a client's requests are counted under: its address, or its /64 network for IPv6.</summary>
    public static IPAddress PartitionKey(IPAddress? address)
    {
        if (address is null)
        {
            return IPAddress.None;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address;
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes);
    }

    /// <summary>At most <paramref name="permitLimit"/> API requests in flight at once, with no queue; other paths are not limited.</summary>
    public static PartitionedRateLimiter<HttpContext> CreateGlobalLimiter(int permitLimit) =>
        PartitionedRateLimiter.Create<HttpContext, string>(context => ApiErrorHandling.IsApiRequest(context)
            ? RateLimitPartition.GetConcurrencyLimiter("api", _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = permitLimit,
                QueueLimit = 0,
            })
            : RateLimitPartition.GetNoLimiter(string.Empty));

    private static void Configure(RateLimiterOptions limiter, ApiRateLimitOptions settings)
    {
        limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        limiter.GlobalLimiter = CreateGlobalLimiter(settings.ConcurrentApiRequests);
        limiter.AddPolicy(PassengerPolicy, context => PerClient(context, settings.Passenger));
        limiter.AddPolicy(ReadPolicy, context => PerClient(context, settings.Read));
        limiter.AddPolicy(SignInPolicy, context => PerClient(context, settings.SignIn));
        limiter.AddPolicy(StaffPolicy, context => PerAccount(context, settings.Staff));
        limiter.OnRejected = WriteRejectionAsync;
    }

    private static RateLimitPartition<IPAddress> PerClient(HttpContext context, TokenBucketSettings bucket) =>
        RateLimitPartition.GetTokenBucketLimiter(PartitionKey(context.Connection.RemoteIpAddress), _ => Bucket(bucket));

    /// <summary>One bucket per account ID once signed in, so staff behind one address do not share a budget.</summary>
    private static RateLimitPartition<string> PerAccount(HttpContext context, TokenBucketSettings bucket) =>
        RateLimitPartition.GetTokenBucketLimiter(
            context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } account
                ? "account:" + account
                : "address:" + PartitionKey(context.Connection.RemoteIpAddress),
            _ => Bucket(bucket));

    private static TokenBucketRateLimiterOptions Bucket(TokenBucketSettings bucket) => new()
    {
        TokenLimit = bucket.TokenLimit,
        TokensPerPeriod = bucket.TokensPerPeriod,
        ReplenishmentPeriod = bucket.ReplenishmentPeriod,
        QueueLimit = 0,
        AutoReplenishment = true,
    };

    private static async ValueTask WriteRejectionAsync(OnRejectedContext rejected, CancellationToken cancellationToken)
    {
        var context = rejected.HttpContext;
        var retryAfter = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait)
            ? Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))
            : DefaultRetryAfterSeconds;
        context.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);

        var problems = context.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails =
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many requests",
                Detail = "Slow down and try again after the time given in the Retry-After header.",
            },
        });
    }
}

/// <summary>Rate limits, bound from the <c>RateLimiting</c> configuration section.</summary>
internal sealed class ApiRateLimitOptions
{
    public const string Section = "RateLimiting";

    /// <summary>Per client: a burst of 20 calls, then 10 per second. Pressing every button twice fits; a script does not.</summary>
    public TokenBucketSettings Passenger { get; set; } = new() { TokenLimit = 20, TokensPerPeriod = 10 };

    /// <summary>Per client: a burst of 60 reads, then 30 per second. Live updates come over the hub, not by polling.</summary>
    public TokenBucketSettings Read { get; set; } = new() { TokenLimit = 60, TokensPerPeriod = 30 };

    /// <summary>Per client: 10 attempts, then one every 6 seconds. A person mistyping fits; password guessing does not.</summary>
    public TokenBucketSettings SignIn { get; set; } = new() { TokenLimit = 10, TokensPerPeriod = 1, ReplenishmentPeriod = TimeSpan.FromSeconds(6) };

    /// <summary>Per account: a burst of 30 requests, then 5 per second. The admin view needs a few per minute.</summary>
    public TokenBucketSettings Staff { get; set; } = new() { TokenLimit = 30, TokensPerPeriod = 5 };

    /// <summary>API requests in flight at once across all clients; beyond it, requests are refused rather than queued.</summary>
    [System.ComponentModel.DataAnnotations.Range(1, 100_000)]
    public int ConcurrentApiRequests { get; set; } = 256;
}

internal sealed class TokenBucketSettings
{
    [System.ComponentModel.DataAnnotations.Range(1, 1_000_000)]
    public int TokenLimit { get; set; }

    [System.ComponentModel.DataAnnotations.Range(1, 1_000_000)]
    public int TokensPerPeriod { get; set; }

    public TimeSpan ReplenishmentPeriod { get; set; } = TimeSpan.FromSeconds(1);
}
