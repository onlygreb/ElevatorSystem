using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using ElevatorSystem.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ElevatorSystem.Integration.Tests;

/// <summary>
/// The API in distributed mode on the in-memory test server, pointed at one test's broker virtual host. Its
/// logs are kept and written to the test output when the host is disposed.
/// </summary>
public sealed class ApiHost(TestEnvironment environment) : WebApplicationFactory<Program>
{
    private readonly ConcurrentQueue<string> _logs = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("Hosting:Mode", "Distributed");
        builder.UseSetting("ConnectionStrings:messaging", environment.BrokerUri.ToString());
        builder.UseSetting("Messaging:RecoveryInterval", "00:00:01");
        builder.UseSetting("Messaging:ConnectTimeout", "00:00:02");
        // Generous: with every test project running at once, a starved broker can take seconds to confirm.
        builder.UseSetting("Messaging:PublishTimeout", "00:00:10");
        builder.UseSetting("Elevator:TravelTimePerFloor", Timings.TravelTimePerFloor);
        builder.UseSetting("Elevator:DoorOpenDuration", Timings.DoorOpenDuration);
        builder.UseSetting("RateLimiting:Passenger:TokenLimit", "100000");
        builder.UseSetting("RateLimiting:Read:TokenLimit", "100000");
        builder.ConfigureLogging(logging => logging.ClearProviders().AddProvider(new QueueLoggerProvider(_logs)));
    }

    public async Task<HttpResponseMessage> CallAsync(string route, string json)
    {
        using var client = CreateClient();
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.PostAsync(new Uri(route, UriKind.Relative), content, TestContext.Current.CancellationToken);
    }

    /// <summary>Places a call and returns the request id the API answered with.</summary>
    public async Task<Guid> AcceptedCallAsync(string route, string json)
    {
        using var response = await CallAsync(route, json);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var accepted = await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.RequestAcceptedResponse, TestContext.Current.CancellationToken);
        return accepted.ShouldNotBeNull().RequestId;
    }

    /// <summary>The status <c>GET /api/v1/elevator</c> answers, or <see langword="null"/> while it is not known.</summary>
    public async Task<ElevatorStatusDto?> StatusAsync()
    {
        using var client = CreateClient();
        using var response = await client.GetAsync(new Uri(ApiRoutes.Elevator, UriKind.Relative), TestContext.Current.CancellationToken);
        return response.StatusCode == HttpStatusCode.OK
            ? await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.ElevatorStatusDto, TestContext.Current.CancellationToken)
            : null;
    }

    public Task<ElevatorStatusDto> WaitForStatusAsync(Func<ElevatorStatusDto, bool> match, string because, TimeSpan? timeout = null) =>
        Eventually.ReturnsAsync(async () => await StatusAsync() is { } status && match(status) ? status : null, because, timeout);

    /// <summary>A SignalR client configured like the browser's, connected over WebSockets through the test server.</summary>
    public async Task<HubClient> ConnectHubAsync()
    {
        var server = Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, ElevatorHub.Path), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.WebSocketFactory = async (context, cancellationToken) =>
                    await server.CreateWebSocketClient().ConnectAsync(context.Uri, cancellationToken);
            })
            .AddJsonProtocol(options => options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, ContractsJsonContext.Default))
            .Build();
        var client = new HubClient(connection);
        await connection.StartAsync(TestContext.Current.CancellationToken);
        return client;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        var output = TestContext.Current.TestOutputHelper;
        if (output is not null)
        {
            foreach (var line in _logs)
            {
                output.WriteLine($"[api] {line}");
            }
        }
    }

    private sealed class QueueLoggerProvider(ConcurrentQueue<string> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new QueueLogger(logs, categoryName);

        public void Dispose()
        {
        }
    }

    private sealed class QueueLogger(ConcurrentQueue<string> logs, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                logs.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} {logLevel} {category}[{eventId.Id}]: {formatter(state, exception)} {exception?.GetType().Name}");
            }
        }
    }
}

/// <summary>What one hub connection received, in order.</summary>
public sealed class HubClient(HubConnection connection) : IAsyncDisposable
{
    private readonly ConcurrentQueue<ElevatorStatusDto> _statuses = Subscribe<ElevatorStatusDto>(connection, nameof(IElevatorClient.StatusChanged));
    private readonly ConcurrentQueue<ElevatorActivityDto> _activities = Subscribe<ElevatorActivityDto>(connection, nameof(IElevatorClient.ActivityOccurred));

    public IReadOnlyList<ElevatorStatusDto> Statuses => [.. _statuses];

    public IReadOnlyList<ElevatorActivityDto> Activities => [.. _activities];

    /// <param name="match">The activity to wait for.</param>
    /// <param name="because">What is awaited, for the failure message.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="fromIndex">Ignore activities received before this position, for example before a crash.</param>
    public Task<ElevatorActivityDto> WaitForActivityAsync(Func<ElevatorActivityDto, bool> match, string because, TimeSpan? timeout = null, int fromIndex = 0) =>
        Eventually.ReturnsAsync(() => Task.FromResult(Activities.Skip(fromIndex).FirstOrDefault(match)), because, timeout);

    public Task<ElevatorStatusDto> WaitForStatusAsync(Func<ElevatorStatusDto, bool> match, string because, TimeSpan? timeout = null) =>
        Eventually.ReturnsAsync(() => Task.FromResult(Statuses.FirstOrDefault(match)), because, timeout);

    public async ValueTask DisposeAsync() => await connection.DisposeAsync();

    private static ConcurrentQueue<T> Subscribe<T>(HubConnection connection, string method)
    {
        var received = new ConcurrentQueue<T>();
        connection.On<T>(method, message => received.Enqueue(message));
        return received;
    }
}
