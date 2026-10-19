using ElevatorSystem.Application.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using Log = ElevatorSystem.Infrastructure.Messaging.MessagingLog;

namespace ElevatorSystem.Infrastructure.Messaging;

/// <summary>
/// The service's one connection to RabbitMQ, shared by its publishers and consumers. It connects on first use (or when
/// <see cref="RabbitMqConnectionStarter"/> starts it), declares the topology on every new connection, and relies on
/// the client's automatic recovery after that, which also restores channels, queues, bindings and consumers.
/// It owns the publishing channel, which has publisher confirms on.
/// </summary>
public sealed class RabbitMqConnection : IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

    private readonly ILogger<RabbitMqConnection> _logger;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _publishChannelGate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _publishChannel;
    private bool _disposed;

    public RabbitMqConnection(IOptions<MessagingOptions> options, ILogger<RabbitMqConnection> logger, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        Options = options.Value;
        _logger = logger;
        TimeProvider = timeProvider;
    }

    public MessagingOptions Options { get; }

    public TimeProvider TimeProvider { get; }

    /// <summary>True while connected; false before the first connection and while recovering from a lost one.</summary>
    public bool IsConnected => Volatile.Read(ref _connection)?.IsOpen == true;

    /// <summary>
    /// The connection, connecting first if there is none yet. A connection being recovered is returned as it is;
    /// opening a channel on it fails until recovery completes.
    /// </summary>
    /// <exception cref="MessagingUnavailableException">The broker could not be reached or the topology could not be declared.</exception>
    public async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _connection) is { } existing)
        {
            return existing;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is null)
            {
                var connection = await ConnectAsync(cancellationToken);
                Volatile.Write(ref _connection, connection);
            }

            return _connection;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>Publishes and waits for the broker's confirm, within <see cref="MessagingOptions.PublishTimeout"/>.</summary>
    /// <exception cref="MessagingUnavailableException">Not connected, nacked, unroutable (when mandatory) or not confirmed in time.</exception>
    internal async Task PublishAsync(string exchange, string routingKey, bool mandatory, Guid messageId, string type, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        using var activity = RabbitMqTelemetry.StartPublish(exchange, messageId, type);
        var properties = RabbitMqEnvelope.CreateProperties(messageId, type, TimeProvider.GetUtcNow());
        using var timeout = new CancellationTokenSource(Options.PublishTimeout, TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        string reason;
        Exception? failure;
        try
        {
            var channel = await GetPublishChannelAsync(linked.Token);
            await channel.BasicPublishAsync(exchange, routingKey, mandatory, properties, body, linked.Token);
            Log.Published(_logger, messageId, type);
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            (reason, failure) = ($"no confirm within {Options.PublishTimeout}", exception);
        }
        catch (PublishException exception)
        {
            (reason, failure) = (exception.IsReturn ? "no queue is bound for it" : "the broker refused it", exception);
        }
        catch (MessagingUnavailableException exception)
        {
            (reason, failure) = ("the broker is unreachable", exception);
        }
        // Whatever else went wrong (closed channel, lost connection), the message was not confirmed.
        catch (Exception exception) when (exception is not ObjectDisposedException)
        {
            (reason, failure) = ("the connection failed", exception);
        }

        activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, reason);
        Log.PublishFailed(_logger, failure is MessagingUnavailableException ? null : failure, messageId, type, reason);
        throw new MessagingUnavailableException($"The message broker did not confirm {type} {messageId}: {reason}.", failure);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_publishChannel is { } channel)
        {
            await CloseQuietlyAsync(channel);
        }

        if (_connection is { } connection)
        {
            try
            {
                await connection.CloseAsync();
            }
            catch (Exception exception) when (exception is OperationInterruptedException or IOException or TimeoutException or OperationCanceledException)
            {
                // Already gone; nothing left to close.
            }

            await connection.DisposeAsync();
        }

        _connectGate.Dispose();
        _publishChannelGate.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    internal static async Task CloseQuietlyAsync(IChannel channel)
    {
        try
        {
            await channel.DisposeAsync();
        }
        catch (Exception exception) when (exception is OperationInterruptedException or IOException or TimeoutException or OperationCanceledException)
        {
            // The channel or its connection is already closed.
        }
    }

    private async Task<IChannel> GetPublishChannelAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _publishChannel) is { IsOpen: true } open)
        {
            return open;
        }

        await _publishChannelGate.WaitAsync(cancellationToken);
        try
        {
            if (_publishChannel is { IsOpen: true } channel)
            {
                return channel;
            }

            var connection = await GetConnectionAsync(cancellationToken);
            if (!connection.IsOpen)
            {
                // Recovery restores the existing channel; until then nothing can be confirmed.
                throw new MessagingUnavailableException("The connection to the message broker is being recovered.");
            }

            if (_publishChannel is { } closed)
            {
                // Closed by the broker on a channel error while the connection stayed up: it will not be recovered.
                await CloseQuietlyAsync(closed);
            }

            var created = await connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                cancellationToken);
            Volatile.Write(ref _publishChannel, created);
            return created;
        }
        finally
        {
            _publishChannelGate.Release();
        }
    }

    private async Task<IConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var factory = CreateFactory(Options);
        var endpoint = Options.Describe();
        using var timeout = new CancellationTokenSource(Options.ConnectTimeout, TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        IConnection? connection = null;
        try
        {
            connection = await factory.CreateConnectionAsync(Options.ClientName, linked.Token);
            await using (var channel = await connection.CreateChannelAsync(cancellationToken: linked.Token))
            {
                await RabbitMqTopology.DeclareAsync(channel, linked.Token);
            }

            connection.ConnectionShutdownAsync += OnConnectionShutdownAsync;
            connection.RecoverySucceededAsync += OnRecoverySucceededAsync;
            connection.ConnectionRecoveryErrorAsync += OnRecoveryErrorAsync;
            Log.Connected(_logger, endpoint);
            return connection;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }

            Log.ConnectFailed(_logger, exception, endpoint);
            throw new MessagingUnavailableException($"Could not connect to the message broker at {endpoint}.", exception);
        }
    }

    private static ConnectionFactory CreateFactory(MessagingOptions options)
    {
        var factory = new ConnectionFactory
        {
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            NetworkRecoveryInterval = options.RecoveryInterval,
            RequestedConnectionTimeout = options.ConnectTimeout,
            RequestedHeartbeat = Heartbeat,
            ClientProvidedName = options.ClientName,
            // One message at a time per consumer: commands are handled in queue order.
            ConsumerDispatchConcurrency = 1,
        };

        if (options.Uri is { } uri)
        {
            factory.Uri = uri;
        }
        else
        {
            factory.HostName = options.Host;
            factory.Port = options.Port;
            factory.VirtualHost = options.VirtualHost;
            factory.UserName = options.UserName!;
            factory.Password = options.Password!;
            if (options.UseTls)
            {
                factory.Ssl = new SslOption(options.Host) { Enabled = true };
            }
        }

        return factory;
    }

    private Task OnConnectionShutdownAsync(object sender, ShutdownEventArgs args)
    {
        if (args.Initiator != ShutdownInitiator.Application)
        {
            Log.ConnectionLost(_logger, $"{args.Initiator}: {args.ReplyCode}");
        }

        return Task.CompletedTask;
    }

    private Task OnRecoverySucceededAsync(object sender, AsyncEventArgs args)
    {
        Log.ConnectionRecovered(_logger);
        return Task.CompletedTask;
    }

    private Task OnRecoveryErrorAsync(object sender, ConnectionRecoveryErrorEventArgs args)
    {
        Log.RecoveryFailed(_logger, args.Exception);
        return Task.CompletedTask;
    }
}
