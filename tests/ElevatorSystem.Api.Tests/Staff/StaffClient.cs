using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElevatorSystem.Contracts;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;

namespace ElevatorSystem.Api.Tests.Staff;

/// <summary>A test account; passwords are test values that exist only in this project.</summary>
internal sealed record StaffAccount(string Email, string Password, StaffRole Role);

internal static class StaffAccounts
{
    public static readonly StaffAccount Admin = new("admin@elevator.test", "correct horse battery staple", StaffRole.Admin);
    public static readonly StaffAccount Auditor = new("auditor@elevator.test", "auditors read every line twice", StaffRole.Auditor);
}

/// <summary>
/// A browser-like client for the staff API: HTTPS (the cookies are <c>Secure</c>), a cookie jar, and the
/// antiforgery token fetched and sent the way the UI does.
/// </summary>
internal sealed class StaffClient : IDisposable
{
    private static readonly Uri BaseAddress = new("https://localhost");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CookieContainer _cookies = new();
    private AntiforgeryTokenDto? _antiforgery;

    private StaffClient(ApiFactory factory)
    {
        Http = factory.CreateDefaultClient(BaseAddress, new CookieContainerHandler(_cookies));
    }

    public HttpClient Http { get; }

    /// <summary>The authenticator key registered by <see cref="EnableTwoFactorAsync"/>, if it ran.</summary>
    public string? AuthenticatorKey { get; private set; }

    /// <summary>The <c>Cookie</c> header this client sends, for connecting to the hub as the same session.</summary>
    public string CookieHeader => _cookies.GetCookieHeader(BaseAddress);

    public static StaffClient Anonymous(ApiFactory factory) => new(factory);

    /// <summary>Signed in with the password only: enough to register a second factor, nothing else.</summary>
    public static async Task<StaffClient> SignedInWithPasswordAsync(ApiFactory factory, StaffAccount account)
    {
        var client = new StaffClient(factory);
        using var response = await client.SignInAsync(account.Email, account.Password);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return client;
    }

    /// <summary>Signed in, with an authenticator registered and this session verified with it.</summary>
    public static async Task<StaffClient> SignedInWithTwoFactorsAsync(ApiFactory factory, StaffAccount account)
    {
        var client = await SignedInWithPasswordAsync(factory, account);
        await client.EnableTwoFactorAsync();
        return client;
    }

    public Task<HttpResponseMessage> SignInAsync(string email, string password, string? code = null, string? recoveryCode = null) =>
        PostAsync(ApiRoutes.SignIn, new SignInRequest(email, password, code, recoveryCode));

    /// <summary>Registers a new authenticator key and enables two-factor authentication; returns the recovery codes.</summary>
    public async Task<IReadOnlyList<string>> EnableTwoFactorAsync()
    {
        using var setup = await PostAsync(ApiRoutes.TwoFactorSetup);
        setup.StatusCode.ShouldBe(HttpStatusCode.OK);
        AuthenticatorKey = (await setup.Content.ReadFromJsonAsync(ContractsJsonContext.Default.TwoFactorSetupDto, Tests.Http.Cancellation))!.SharedKey;

        using var enabled = await PostAsync(ApiRoutes.TwoFactorEnable, new EnableTwoFactorRequest(Totp.Code(AuthenticatorKey)));
        enabled.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await enabled.Content.ReadFromJsonAsync(ContractsJsonContext.Default.RecoveryCodesDto, Tests.Http.Cancellation))!.RecoveryCodes;
    }

    /// <summary>POSTs <paramref name="body"/> as JSON with the antiforgery token, fetching a fresh one after sign-in changes.</summary>
    public async Task<HttpResponseMessage> PostAsync(string route, object? body = null, bool antiforgery = true, IDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(route, UriKind.Relative))
        {
            Content = JsonContent(body ?? new { }),
        };
        if (antiforgery)
        {
            var token = await AntiforgeryAsync();
            request.Headers.Add(token.HeaderName, token.RequestToken);
        }

        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.Add(name, value);
        }

        var response = await Http.SendAsync(request, Tests.Http.Cancellation);
        if (route is ApiRoutes.SignIn or ApiRoutes.SignOut or ApiRoutes.TwoFactorEnable)
        {
            // The antiforgery token belongs to the signed-in user, so a new session needs a new token.
            _antiforgery = null;
        }

        return response;
    }

    public Task<HttpResponseMessage> GetAsync(string route) => Http.GetRouteAsync(route);

    public void Dispose() => Http.Dispose();

    private async Task<AntiforgeryTokenDto> AntiforgeryAsync()
    {
        if (_antiforgery is null)
        {
            using var response = await Http.GetRouteAsync(ApiRoutes.Antiforgery);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            _antiforgery = await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.AntiforgeryTokenDto, Tests.Http.Cancellation);
        }

        return _antiforgery!;
    }

    private static StringContent JsonContent(object body) =>
        new(JsonSerializer.Serialize(body, body.GetType(), Json), Encoding.UTF8, "application/json");
}

/// <summary>RFC 6238 time-based one-time passwords (SHA-1, 30 s, 6 digits), as authenticator apps compute them.</summary>
internal static class Totp
{
    public static string Code(string base32Key, DateTimeOffset? at = null)
    {
        var counter = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;
        return Code(Base32Decode(base32Key), counter);
    }

    public static string Code(byte[] key, long counter)
    {
        Span<byte> message = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(message, counter);
#pragma warning disable CA5350 // RFC 6238 authenticator codes are defined over HMAC-SHA-1; this mirrors what the apps do.
        var hash = HMACSHA1.HashData(key, message);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var binary = (BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset)) & 0x7FFFFFFF) % 1_000_000;
        return binary.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] Base32Decode(string text)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var clean = text.Replace(" ", "", StringComparison.Ordinal).TrimEnd('=').ToUpperInvariant();
        var bytes = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var character in clean)
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(character, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}
