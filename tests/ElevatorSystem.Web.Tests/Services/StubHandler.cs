using System.Net;
using System.Text;

namespace ElevatorSystem.Web.Tests.Services;

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body);

/// <summary>Answers every request with one canned response (or failure) and records what was sent. No network.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage? _response;
    private readonly Exception? _failure;

    public StubHandler(HttpResponseMessage response) => _response = response;

    public StubHandler(Exception failure) => _failure = failure;

    public List<RecordedRequest> Requests { get; } = [];

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public HttpClient Client() => new(this) { BaseAddress = new Uri("https://elevator.test/") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body));
        return _failure is null ? _response! : throw _failure;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _response?.Dispose();
        }

        base.Dispose(disposing);
    }
}
