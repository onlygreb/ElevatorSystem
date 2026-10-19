using System.Text.Json;
using System.Threading.Channels;
using ElevatorSystem.Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace ElevatorSystem.Api.Tests.RealTime;

/// <summary>
/// A SignalR client connected through the in-memory test server over WebSockets, configured the way
/// the browser client is: the JSON protocol with <see cref="ContractsJsonContext"/> first in the chain.
/// </summary>
internal sealed class HubClient : IAsyncDisposable
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    private readonly Channel<ElevatorStatusDto> _statuses = Channel.CreateUnbounded<ElevatorStatusDto>();
    private readonly Channel<ElevatorActivityDto> _activities = Channel.CreateUnbounded<ElevatorActivityDto>();
    private readonly Channel<JsonElement> _rawStatuses = Channel.CreateUnbounded<JsonElement>();

    private HubClient(HubConnection connection)
    {
        Connection = connection;
        connection.On<ElevatorStatusDto>(nameof(IElevatorClient.StatusChanged), status => _statuses.Writer.TryWrite(status));
        connection.On<ElevatorActivityDto>(nameof(IElevatorClient.ActivityOccurred), activity => _activities.Writer.TryWrite(activity));
    }

    public HubConnection Connection { get; }

    /// <param name="factory">The host to connect to.</param>
    /// <param name="captureRawStatus">
    /// Receive status messages as plain JSON instead of contracts, to inspect the wire format itself.
    /// </param>
    public static async Task<HubClient> ConnectAsync(ApiFactory factory, bool captureRawStatus = false)
    {
        var server = factory.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, ElevatorHub.Path), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.WebSocketFactory = async (context, cancellationToken) =>
                    await server.CreateWebSocketClient().ConnectAsync(context.Uri, cancellationToken);
            })
            .AddJsonProtocol(options =>
            {
                // With the context alone in the chain the client can read contracts only, which is all the
                // browser needs; the raw mode keeps the default resolver so it can read a JsonElement.
                if (!captureRawStatus)
                {
                    options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, ContractsJsonContext.Default);
                }
            })
            .Build();

        var client = new HubClient(connection);
        if (captureRawStatus)
        {
            connection.Remove(nameof(IElevatorClient.StatusChanged));
            connection.On<JsonElement>(nameof(IElevatorClient.StatusChanged), status => client._rawStatuses.Writer.TryWrite(status));
        }

        await connection.StartAsync(TestContext.Current.CancellationToken);
        return client;
    }

    public Task<ElevatorStatusDto> NextStatusAsync(Func<ElevatorStatusDto, bool>? match = null) => NextAsync(_statuses, match, "status");

    public Task<ElevatorActivityDto> NextActivityAsync(Func<ElevatorActivityDto, bool>? match = null) => NextAsync(_activities, match, "activity");

    public Task<JsonElement> NextRawStatusAsync() => NextAsync(_rawStatuses, null, "raw status");

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();

    private static async Task<T> NextAsync<T>(Channel<T> channel, Func<T, bool>? match, string what)
    {
        using var guard = new CancellationTokenSource(Guard);
        var skipped = new List<T>();
        try
        {
            while (true)
            {
                var message = await channel.Reader.ReadAsync(guard.Token);
                if (match?.Invoke(message) ?? true)
                {
                    return message;
                }

                skipped.Add(message);
            }
        }
        catch (OperationCanceledException) when (guard.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"No matching {what} received within {Guard.TotalSeconds} s. Received instead:{Environment.NewLine}{string.Join(Environment.NewLine, skipped)}");
        }
    }
}
