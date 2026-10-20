using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Resources;
using Microsoft.Extensions.Localization;

namespace ElevatorSystem.Web.Services;

/// <summary>The steps of an anonymous passenger (ADR-0026). Every step is accepted with a request id or refused at once.</summary>
public interface IPassengerApiClient
{
    /// <summary>Arrives as a new passenger on the lowest floor.</summary>
    Task<(PassengerAcceptedResponse? Accepted, RequestOutcome Outcome)> JoinAsync(CancellationToken cancellationToken = default);

    /// <summary>Where the passenger is, or <see langword="null"/> when the building does not know them (any more).</summary>
    Task<PassengerDto?> GetAsync(Guid passengerId, CancellationToken cancellationToken = default);

    Task<RequestOutcome> CallElevatorAsync(Guid passengerId, CallDirection direction, CancellationToken cancellationToken = default);

    Task<RequestOutcome> BoardAsync(Guid passengerId, CancellationToken cancellationToken = default);

    Task<RequestOutcome> ChooseFloorAsync(Guid passengerId, int floor, CancellationToken cancellationToken = default);

    Task<RequestOutcome> LeaveAsync(Guid passengerId, CancellationToken cancellationToken = default);
}

/// <summary>Calls the passenger endpoints on the page's own origin; refusals become localised messages.</summary>
internal sealed class PassengerApiClient(HttpClient http, IStringLocalizer<SharedResource> text) : IPassengerApiClient
{
    private static readonly ContractsJsonContext Json = ContractsJsonContext.Default;

    public async Task<(PassengerAcceptedResponse? Accepted, RequestOutcome Outcome)> JoinAsync(CancellationToken cancellationToken = default)
    {
        PassengerAcceptedResponse? accepted = null;
        var outcome = await SendAsync(
            () => http.PostAsync(ApiRoutes.Passengers, Empty(), cancellationToken),
            async content => (accepted = await content.ReadFromJsonAsync(Json.PassengerAcceptedResponse, cancellationToken))?.RequestId,
            cancellationToken);
        return (accepted, outcome);
    }

    public async Task<PassengerDto?> GetAsync(Guid passengerId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.GetAsync(ApiRoutes.PassengerRoute(passengerId), cancellationToken);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync(Json.PassengerDto, cancellationToken) : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public Task<RequestOutcome> CallElevatorAsync(Guid passengerId, CallDirection direction, CancellationToken cancellationToken = default) =>
        Step(() => http.PostAsJsonAsync(ApiRoutes.PassengerRoute(passengerId) + "/hall-call", new PassengerHallCallRequest(direction), Json.PassengerHallCallRequest, cancellationToken), cancellationToken);

    public Task<RequestOutcome> BoardAsync(Guid passengerId, CancellationToken cancellationToken = default) =>
        Step(() => http.PostAsync(ApiRoutes.PassengerRoute(passengerId) + "/board", Empty(), cancellationToken), cancellationToken);

    public Task<RequestOutcome> ChooseFloorAsync(Guid passengerId, int floor, CancellationToken cancellationToken = default) =>
        Step(() => http.PostAsJsonAsync(ApiRoutes.PassengerRoute(passengerId) + "/car-call", new PassengerCarCallRequest(floor), Json.PassengerCarCallRequest, cancellationToken), cancellationToken);

    public Task<RequestOutcome> LeaveAsync(Guid passengerId, CancellationToken cancellationToken = default) =>
        Step(() => http.PostAsync(ApiRoutes.PassengerRoute(passengerId) + "/leave", Empty(), cancellationToken), cancellationToken);

    private static StringContent Empty() => new("{}", System.Text.Encoding.UTF8, "application/json");

    private Task<RequestOutcome> Step(Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken) =>
        SendAsync(send, async content => (await content.ReadFromJsonAsync(Json.PassengerAcceptedResponse, cancellationToken))?.RequestId, cancellationToken);

    private async Task<RequestOutcome> SendAsync(Func<Task<HttpResponseMessage>> send, Func<HttpContent, Task<Guid?>> read, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await send();
            if (response.IsSuccessStatusCode)
            {
                return await read(response.Content) is { } requestId
                    ? RequestOutcome.Accepted(requestId)
                    : RequestOutcome.Refused(null, text[Text.RefusedUnexpected]);
            }

            return response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => RequestOutcome.Refused(null, text[Text.RefusedTooManyRequests]),
                HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout =>
                    RequestOutcome.Refused(RejectionReason.Busy, text[Text.RefusedUnavailable]),
                HttpStatusCode.Conflict => RequestOutcome.Refused(RejectionReason.OutOfService, text[Text.RefusedOutOfService]),
                _ => RequestOutcome.Refused(null, text[Text.RefusedUnexpected]),
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return RequestOutcome.Refused(null, text[Text.RefusedUnavailable]);
        }
    }
}
