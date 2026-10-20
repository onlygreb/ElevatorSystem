using System.Text.Json;
using ElevatorSystem.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace ElevatorSystem.Web.Services;

/// <summary>
/// SignalR connection to <see cref="ElevatorHub.Path"/>. Feeds every status and activity into the
/// <see cref="ElevatorStateStore"/> and reports the connection state there. Reconnects automatically with
/// backoff and never gives up, so a lobby screen recovers on its own after a server restart.
/// </summary>
internal sealed partial class ElevatorHubClient : IElevatorHubClient, IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly ElevatorStateStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ElevatorHubClient> _logger;
    private readonly CancellationTokenSource _disposing = new();
    private CancellationTokenSource? _attempts;
    private Task? _connecting;
    private bool _restarting;

    public ElevatorHubClient(Uri hubUrl, ElevatorStateStore store, TimeProvider timeProvider, ILogger<ElevatorHubClient> logger)
    {
        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect(new ReconnectBackoff())
            .AddJsonProtocol(options => ConfigureJson(options.PayloadSerializerOptions))
            .Build();

        _connection.On<ElevatorStatusDto>(nameof(IElevatorClient.StatusChanged), status => store.ApplyStatus(status));
        _connection.On<ElevatorActivityDto>(nameof(IElevatorClient.ActivityOccurred), store.AddActivity);
        _connection.On<BuildingOccupancyDto>(nameof(IElevatorClient.OccupancyChanged), occupancy => store.ApplyOccupancy(occupancy));
        _connection.Reconnecting += _ =>
        {
            store.SetConnection(ConnectionState.Reconnecting);
            return Task.CompletedTask;
        };
        _connection.Reconnected += _ =>
        {
            store.SetConnection(ConnectionState.Connected);
            return Task.CompletedTask;
        };
        _connection.Closed += _ =>
        {
            // Automatic reconnect never gives up, so this happens only when the server ends the connection
            // for good, or on dispose. Start over unless the page is going away.
            if (!_disposing.IsCancellationRequested && !_restarting)
            {
                store.SetConnection(ConnectionState.Reconnecting);
                _connecting = StartConnecting();
            }

            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// Wire format of the hub: the shared <see cref="ContractsJsonContext"/> resolves every contract first, the same
    /// configuration the server uses.
    /// </summary>
    public static void ConfigureJson(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.TypeInfoResolverChain.Insert(0, ContractsJsonContext.Default);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _connecting ??= StartConnecting();
        return Task.CompletedTask;
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        _restarting = true;
        try
        {
            // Only one connect loop at a time: an old one still retrying would fight the new connection.
            if (_attempts is { } attempts)
            {
                await attempts.CancelAsync();
            }

            if (_connecting is { } loop)
            {
                await loop;
            }

            await _connection.StopAsync(cancellationToken);
        }
        finally
        {
            _restarting = false;
        }

        _store.SetConnection(ConnectionState.Reconnecting);
        _connecting = StartConnecting();
    }

    public async ValueTask DisposeAsync()
    {
        await _disposing.CancelAsync();
        await _connection.DisposeAsync();
        _attempts?.Dispose();
        _disposing.Dispose();
    }

    /// <summary>Starts a connect loop that the next restart (or dispose) can cancel.</summary>
    private Task StartConnecting()
    {
        _attempts?.Dispose();
        _attempts = CancellationTokenSource.CreateLinkedTokenSource(_disposing.Token);
        return ConnectAsync(_attempts.Token);
    }

    /// <summary>The first connection: <c>WithAutomaticReconnect</c> only covers connections that were once established.</summary>
    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; !cancellationToken.IsCancellationRequested; attempt++)
        {
            // Already connected (by an earlier loop, or a reconnect): nothing to start.
            if (_connection.State == HubConnectionState.Connected)
            {
                _store.SetConnection(ConnectionState.Connected);
                return;
            }

            try
            {
                await _connection.StartAsync(cancellationToken);
                _store.SetConnection(ConnectionState.Connected);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or TimeoutException or OperationCanceledException)
            {
                var delay = ReconnectBackoff.Delay(attempt);
                LogConnectFailed(exception.GetType().Name, delay);
                if (attempt > 0)
                {
                    _store.SetConnection(ConnectionState.Reconnecting);
                }

                try
                {
                    await Task.Delay(delay, _timeProvider, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    // Only the exception type is logged: messages can carry URLs, and nothing else is needed to diagnose a retry.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not connect to the elevator hub ({ExceptionType}); retrying in {Delay}.")]
    private partial void LogConnectFailed(string exceptionType, TimeSpan delay);
}
