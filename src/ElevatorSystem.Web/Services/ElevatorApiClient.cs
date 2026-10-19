using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Resources;
using Microsoft.Extensions.Localization;

namespace ElevatorSystem.Web.Services;

/// <summary>
/// Typed client for the elevator REST API on the page's own origin. Serialises with <see cref="ContractsJsonContext"/>
/// and turns every refusal (a ProblemDetails with a <c>reason</c>, a rate limit, an unreachable server) into a
/// localised message. Raw server text is never shown to the passenger.
/// </summary>
internal sealed class ElevatorApiClient(HttpClient http, IStringLocalizer<SharedResource> text) : IElevatorApiClient
{
    private static readonly HashSet<string> RejectionReasonNames = [.. Enum.GetNames<RejectionReason>()];

    public async Task<ElevatorConfigurationDto> GetConfigurationAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync(ApiRoutes.Configuration, ContractsJsonContext.Default.ElevatorConfigurationDto, cancellationToken)
        ?? throw new InvalidOperationException("The API returned an empty configuration.");

    public async Task<ElevatorStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync(ApiRoutes.Elevator, ContractsJsonContext.Default.ElevatorStatusDto, cancellationToken)
        ?? throw new InvalidOperationException("The API returned an empty status.");

    public Task<RequestOutcome> RequestElevatorAsync(int floor, CallDirection direction, CancellationToken cancellationToken = default) =>
        SendAsync(
            () => http.PostAsJsonAsync(ApiRoutes.HallCalls, new HallCallRequest(floor, direction), ContractsJsonContext.Default.HallCallRequest, cancellationToken),
            cancellationToken);

    public Task<RequestOutcome> RequestDestinationAsync(int floor, CancellationToken cancellationToken = default) =>
        SendAsync(
            () => http.PostAsJsonAsync(ApiRoutes.CarCalls, new CarCallRequest(floor), ContractsJsonContext.Default.CarCallRequest, cancellationToken),
            cancellationToken);

    private async Task<RequestOutcome> SendAsync(Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await send();
        }
        catch (HttpRequestException)
        {
            return Refused(null, Text.RefusedUnavailable);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient's own timeout, not the caller cancelling.
            return Refused(null, Text.RefusedUnavailable);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                var accepted = await ReadAcceptedAsync(response.Content, cancellationToken);
                return accepted is null ? Refused(null, Text.RefusedUnexpected) : RequestOutcome.Accepted(accepted.RequestId);
            }

            if (await ReadRejectionReasonAsync(response.Content, cancellationToken) is { } reason)
            {
                return Refused(reason, MessageKeyFor(reason));
            }

            return response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => Refused(null, Text.RefusedTooManyRequests),
                HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
                    Refused(null, Text.RefusedUnavailable),
                _ => Refused(null, Text.RefusedUnexpected),
            };
        }
    }

    /// <summary>The <see cref="Text"/> key of the passenger-facing explanation for <paramref name="reason"/>.</summary>
    internal static string MessageKeyFor(RejectionReason reason) => reason switch
    {
        RejectionReason.InvalidFloor => Text.RefusedInvalidFloor,
        RejectionReason.InvalidDirection => Text.RefusedInvalidDirection,
        RejectionReason.OutOfService => Text.RefusedOutOfService,
        RejectionReason.Busy => Text.RefusedBusy,
        _ => Text.RefusedUnexpected,
    };

    private RequestOutcome Refused(RejectionReason? reason, string key) => RequestOutcome.Refused(reason, text[key]);

    private static async Task<RequestAcceptedResponse?> ReadAcceptedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            return await content.ReadFromJsonAsync(ContractsJsonContext.Default.RequestAcceptedResponse, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads the <c>reason</c> extension of a ProblemDetails body. Only exact enum names count, never numbers.</summary>
    private static async Task<RejectionReason?> ReadRejectionReasonAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            using var problem = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (problem.RootElement.ValueKind == JsonValueKind.Object
                && problem.RootElement.TryGetProperty(ApiRoutes.RejectionReasonExtension, out var reason)
                && reason.ValueKind == JsonValueKind.String
                && reason.GetString() is { } name
                && RejectionReasonNames.Contains(name))
            {
                return Enum.Parse<RejectionReason>(name);
            }
        }
        catch (JsonException)
        {
            // Not JSON (a proxy's HTML error page, for example): fall back to the status code.
        }

        return null;
    }
}
