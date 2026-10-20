using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ElevatorSystem.Contracts;

namespace ElevatorSystem.Web.Services;

/// <summary>Why a staff request did not succeed, as the admin view explains it.</summary>
public enum StaffFailure
{
    InvalidCredentials,
    TwoFactorRequired,
    InvalidTwoFactorCode,
    SessionExpired,
    NotAllowed,
    TooManyRequests,
    Unavailable,
    Unexpected,
}

/// <summary>The value of a staff request, or why there is none.</summary>
public readonly record struct StaffResult<T>(T? Value, StaffFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

/// <summary>Creates <see cref="StaffResult{T}"/> values.</summary>
public static class StaffResult
{
    public static StaffResult<T> Ok<T>(T value) => new(value, null);

    public static StaffResult<T> Fail<T>(StaffFailure failure) => new(default, failure);
}

/// <summary>The staff side of the API: sign-in, two-factor registration, service actions, occupancy and the audit trail.</summary>
public interface IStaffApiClient
{
    /// <summary>The signed-in staff member; <see cref="StaffFailure.SessionExpired"/> when nobody is.</summary>
    Task<StaffResult<StaffSessionDto>> GetSessionAsync(CancellationToken cancellationToken = default);

    Task<StaffResult<StaffSessionDto>> SignInAsync(SignInRequest request, CancellationToken cancellationToken = default);

    Task<StaffResult<bool>> SignOutAsync(CancellationToken cancellationToken = default);

    Task<StaffResult<TwoFactorSetupDto>> SetUpTwoFactorAsync(CancellationToken cancellationToken = default);

    Task<StaffResult<RecoveryCodesDto>> EnableTwoFactorAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>Sends <paramref name="action"/>; a retry with the same <paramref name="idempotencyKey"/> never acts twice.</summary>
    Task<StaffResult<ServiceActionAcceptedResponse>> RequestServiceActionAsync(
        ServiceAction action, Guid idempotencyKey, CancellationToken cancellationToken = default);

    Task<StaffResult<BuildingOccupancyDto>> GetOccupancyAsync(CancellationToken cancellationToken = default);

    Task<StaffResult<AuditPageDto>> GetAuditPageAsync(long? before, CancellationToken cancellationToken = default);

    Task<StaffResult<AuditVerificationDto>> VerifyAuditAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Calls the staff endpoints on the page's own origin. The browser sends the session cookie itself; this client adds
/// the antiforgery token to every <c>POST</c>, fetching a new one after the session changes, and turns refusals into a
/// <see cref="StaffFailure"/>. Raw server text is never shown.
/// </summary>
internal sealed class StaffApiClient(HttpClient http) : IStaffApiClient, IDisposable
{
    private static readonly ContractsJsonContext Json = ContractsJsonContext.Default;

    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private AntiforgeryTokenDto? _antiforgery;

    public Task<StaffResult<StaffSessionDto>> GetSessionAsync(CancellationToken cancellationToken = default) =>
        GetAsync(ApiRoutes.Session, Json.StaffSessionDto, cancellationToken);

    public async Task<StaffResult<StaffSessionDto>> SignInAsync(SignInRequest request, CancellationToken cancellationToken = default)
    {
        var result = await PostAsync(ApiRoutes.SignIn, request, Json.SignInRequest, Json.StaffSessionDto, null, cancellationToken);
        ForgetToken();
        return result;
    }

    public async Task<StaffResult<bool>> SignOutAsync(CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<bool>(
            () => CreatePostAsync(ApiRoutes.SignOut, EmptyBody(), null),
            _ => Task.FromResult(true),
            cancellationToken);
        ForgetToken();
        return result;
    }

    public Task<StaffResult<TwoFactorSetupDto>> SetUpTwoFactorAsync(CancellationToken cancellationToken = default) =>
        SendAsync(() => CreatePostAsync(ApiRoutes.TwoFactorSetup, EmptyBody(), null), Read(Json.TwoFactorSetupDto, cancellationToken), cancellationToken);

    public async Task<StaffResult<RecoveryCodesDto>> EnableTwoFactorAsync(string code, CancellationToken cancellationToken = default)
    {
        var result = await PostAsync(
            ApiRoutes.TwoFactorEnable, new EnableTwoFactorRequest(code), Json.EnableTwoFactorRequest, Json.RecoveryCodesDto, null, cancellationToken);
        if (result.Succeeded)
        {
            ForgetToken();
        }

        return result;
    }

    public Task<StaffResult<ServiceActionAcceptedResponse>> RequestServiceActionAsync(
        ServiceAction action, Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        PostAsync(
            ApiRoutes.ServiceActions,
            new ServiceActionRequest(action),
            Json.ServiceActionRequest,
            Json.ServiceActionAcceptedResponse,
            idempotencyKey.ToString(),
            cancellationToken);

    public Task<StaffResult<BuildingOccupancyDto>> GetOccupancyAsync(CancellationToken cancellationToken = default) =>
        GetAsync(ApiRoutes.Occupancy, Json.BuildingOccupancyDto, cancellationToken);

    public Task<StaffResult<AuditPageDto>> GetAuditPageAsync(long? before, CancellationToken cancellationToken = default) =>
        GetAsync(before is { } sequence ? $"{ApiRoutes.AuditEntries}?before={sequence}" : ApiRoutes.AuditEntries, Json.AuditPageDto, cancellationToken);

    public Task<StaffResult<AuditVerificationDto>> VerifyAuditAsync(CancellationToken cancellationToken = default) =>
        GetAsync(ApiRoutes.AuditVerification, Json.AuditVerificationDto, cancellationToken);

    public void Dispose() => _tokenGate.Dispose();

    /// <summary>Maps a refusal to what the admin view says about it.</summary>
    internal static StaffFailure FailureFor(HttpStatusCode status, string? reason) => (status, reason) switch
    {
        (_, nameof(SignInFailureReason.TwoFactorRequired)) => StaffFailure.TwoFactorRequired,
        (_, nameof(SignInFailureReason.InvalidTwoFactorCode)) => StaffFailure.InvalidTwoFactorCode,
        (HttpStatusCode.Unauthorized, nameof(SignInFailureReason.InvalidCredentials)) => StaffFailure.InvalidCredentials,
        (HttpStatusCode.Unauthorized, _) => StaffFailure.SessionExpired,
        (HttpStatusCode.Forbidden, _) => StaffFailure.NotAllowed,
        (HttpStatusCode.TooManyRequests, _) => StaffFailure.TooManyRequests,
        (HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout, _) => StaffFailure.Unavailable,
        _ => StaffFailure.Unexpected,
    };

    private Task<StaffResult<T>> GetAsync<T>(string route, JsonTypeInfo<T> type, CancellationToken cancellationToken)
        where T : class =>
        SendAsync(() => Task.FromResult(new HttpRequestMessage(HttpMethod.Get, route)), Read(type, cancellationToken), cancellationToken);

    private async Task<StaffResult<TResponse>> PostAsync<TRequest, TResponse>(
        string route,
        TRequest body,
        JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TResponse> responseType,
        string? idempotencyKey,
        CancellationToken cancellationToken)
        where TResponse : class =>
        await SendAsync(() => CreatePostAsync(route, JsonContent.Create(body, requestType), idempotencyKey), Read(responseType, cancellationToken), cancellationToken);

    private async Task<HttpRequestMessage> CreatePostAsync(string route, HttpContent content, string? idempotencyKey)
    {
        var token = await AntiforgeryAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = content };
        request.Headers.Add(token.HeaderName, token.RequestToken);
        if (idempotencyKey is not null)
        {
            request.Headers.Add(ApiRoutes.IdempotencyKeyHeader, idempotencyKey);
        }

        return request;
    }

    private static StringContent EmptyBody() => new("{}", System.Text.Encoding.UTF8, "application/json");

    private static Func<HttpContent, Task<T?>> Read<T>(JsonTypeInfo<T> type, CancellationToken cancellationToken) =>
        content => content.ReadFromJsonAsync(type, cancellationToken);

    /// <summary>
    /// Sends the request; a refused antiforgery token (expired, or from an earlier session) is fetched again and the
    /// request sent once more.
    /// </summary>
    private async Task<StaffResult<T>> SendAsync<T>(
        Func<Task<HttpRequestMessage>> create, Func<HttpContent, Task<T?>> read, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = await create();
                using var response = await http.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return await read(response.Content) is { } value ? StaffResult.Ok(value) : StaffResult.Fail<T>(StaffFailure.Unexpected);
                }

                var reason = await ReasonAsync(response.Content, cancellationToken);
                if (reason == "Antiforgery" && attempt == 0)
                {
                    ForgetToken();
                    continue;
                }

                return StaffResult.Fail<T>(FailureFor(response.StatusCode, reason));
            }
            catch (HttpRequestException)
            {
                return StaffResult.Fail<T>(StaffFailure.Unavailable);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return StaffResult.Fail<T>(StaffFailure.Unavailable);
            }
            catch (JsonException)
            {
                return StaffResult.Fail<T>(StaffFailure.Unexpected);
            }
        }
    }

    private async Task<AntiforgeryTokenDto> AntiforgeryAsync()
    {
        await _tokenGate.WaitAsync();
        try
        {
            return _antiforgery ??= await http.GetFromJsonAsync(ApiRoutes.Antiforgery, Json.AntiforgeryTokenDto)
                ?? throw new JsonException("The API returned an empty antiforgery token.");
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    /// <summary>The token is tied to the signed-in user, so a new session needs a new token.</summary>
    private void ForgetToken() => _antiforgery = null;

    private static async Task<string?> ReasonAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            using var problem = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return problem.RootElement.ValueKind == JsonValueKind.Object
                && problem.RootElement.TryGetProperty(ApiRoutes.RejectionReasonExtension, out var reason)
                && reason.ValueKind == JsonValueKind.String
                    ? reason.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
