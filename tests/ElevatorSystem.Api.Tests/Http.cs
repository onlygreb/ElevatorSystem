using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ElevatorSystem.Contracts;

namespace ElevatorSystem.Api.Tests;

/// <summary>Small helpers for driving the API with exact bytes and reading its answers.</summary>
internal static class Http
{
    public static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string route, string json, string mediaType = "application/json") =>
        client.PostAsync(new Uri(route, UriKind.Relative), new StringContent(json, Encoding.UTF8, mediaType), Cancellation);

    public static Task<HttpResponseMessage> GetRouteAsync(this HttpClient client, string route) =>
        client.GetAsync(new Uri(route, UriKind.Relative), Cancellation);

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Cancellation);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Asserts an RFC 9457 problem response with the given status and returns its body.</summary>
    public static async Task<JsonElement> ShouldBeProblemAsync(this HttpResponseMessage response, HttpStatusCode status)
    {
        response.StatusCode.ShouldBe(status);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/problem+json");
        var problem = await response.ReadJsonAsync();
        problem.GetProperty("status").GetInt32().ShouldBe((int)status);
        problem.GetProperty("title").GetString().ShouldNotBeNullOrWhiteSpace();
        problem.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace("every problem carries a traceId for support");
        return problem;
    }

    /// <summary>The machine-readable <c>reason</c> extension, or null when the problem has none.</summary>
    public static string? Reason(this JsonElement problem) =>
        problem.TryGetProperty(ApiRoutes.RejectionReasonExtension, out var reason) ? reason.GetString() : null;

    public static string? RetryAfter(this HttpResponseMessage response) =>
        response.Headers.RetryAfter is RetryConditionHeaderValue retryAfter
            ? retryAfter.Delta?.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
}
