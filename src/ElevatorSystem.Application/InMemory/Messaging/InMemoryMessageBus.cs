using System.Diagnostics;
using System.Threading.Channels;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Contracts;
using Microsoft.Extensions.Logging;
using Log = ElevatorSystem.Application.InMemory.Messaging.InMemoryMessagingLog;

namespace ElevatorSystem.Application.InMemory.Messaging;

/// <summary>
/// The messaging ports inside one process, on <see cref="Channel{T}"/>, for the console demo, tests and
/// single-process hosting. It keeps the broker's semantics where they make sense (ADR-0018):
/// <list type="bullet">
/// <item>publishing confirms at once, and fails with <see cref="MessagingUnavailableException"/> when the command queue is full</item>
/// <item>one consumer is active at a time; another waits and takes over when it stops</item>
/// <item>a command whose consumer stops before the handler completes is redelivered to the next consumer</item>
/// <item>a handler that throws is retried up to <see cref="MaxDeliveryAttempts"/> times, then the message is dropped and logged</item>
/// <item>every subscriber gets every update; a subscriber that falls behind loses its oldest updates first</item>
/// </list>
/// Nothing survives the process: this is not a substitute for the broker when commands must outlive a restart.
/// </summary>
public sealed class InMemoryMessageBus : IElevatorCommandPublisher, IElevatorCommandConsumer, IElevatorUpdatePublisher, IElevatorUpdateSubscriber
{
    public const int DefaultMaxDeliveryAttempts = 5;
    public const int DefaultCommandCapacity = 1024;
    public const int DefaultSubscriberCapacity = 1024;

    private readonly ILogger<InMemoryMessageBus> _logger;
    private readonly int _subscriberCapacity;
    private readonly Channel<Delivery> _commands;
    // Commands taken by a consumer that stopped before handling them. Only the active consumer reads it.
    private readonly Queue<Delivery> _redeliveries = new();
    // Holds one token: the consumer that takes it is the active one, and gives it back when it stops.
    private readonly Channel<bool> _activeConsumer = Channel.CreateBounded<bool>(1);
    private readonly Lock _subscribersGate = new();
    private Channel<Delivery>[] _subscribers = [];

    public InMemoryMessageBus(
        ILogger<InMemoryMessageBus> logger,
        int maxDeliveryAttempts = DefaultMaxDeliveryAttempts,
        int commandCapacity = DefaultCommandCapacity,
        int subscriberCapacity = DefaultSubscriberCapacity)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDeliveryAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(commandCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(subscriberCapacity, 1);

        _logger = logger;
        MaxDeliveryAttempts = maxDeliveryAttempts;
        _subscriberCapacity = subscriberCapacity;
        _commands = Channel.CreateBounded<Delivery>(new BoundedChannelOptions(commandCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
        });
        _activeConsumer.Writer.TryWrite(true);
    }

    /// <summary>How many times a message is handed to a handler that throws before it is dropped.</summary>
    public int MaxDeliveryAttempts { get; }

    public Task PublishAsync(ElevatorCommandV1 command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        var type = MessageTypes.Of(command);
        using var activity = MessagingActivities.StartPublish(MessagingActivities.CommandsDestination, command.CommandId, type);

        // A full queue is the in-memory equivalent of an unconfirmed publish: the caller must not report it accepted.
        if (!_commands.Writer.TryWrite(new Delivery(command, command.CommandId, type, Activity.Current?.Context ?? default)))
        {
            Log.CommandRefused(_logger, command.CommandId, type);
            throw new MessagingUnavailableException("The in-memory command queue is full.");
        }

        Log.Published(_logger, command.CommandId, type);
        return Task.CompletedTask;
    }

    public Task PublishAsync(ElevatorUpdateV1 update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        return PublishUpdate(update, update.UpdateId, MessageTypes.ElevatorUpdate, cancellationToken);
    }

    public Task PublishAsync(OccupancyUpdateV1 update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        return PublishUpdate(update, update.UpdateId, MessageTypes.OccupancyUpdate, cancellationToken);
    }

    public async Task ConsumeAsync(Func<ElevatorCommandV1, CancellationToken, Task> handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);

        try
        {
            // Like the broker's single-active-consumer queue: a second consumer waits until the first one stops.
            await _activeConsumer.Reader.ReadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var delivery = _redeliveries.Count > 0
                    ? _redeliveries.Dequeue()
                    : await _commands.Reader.ReadAsync(cancellationToken);

                if (!await DeliverAsync(delivery, handle, cancellationToken))
                {
                    // The consumer stopped before the handler completed: the command was never acknowledged.
                    _redeliveries.Enqueue(delivery);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _activeConsumer.Writer.TryWrite(true);
        }
    }

    public async Task SubscribeAsync(
        Func<ElevatorUpdateV1, CancellationToken, Task> onElevatorUpdate,
        Func<OccupancyUpdateV1, CancellationToken, Task> onOccupancyUpdate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onElevatorUpdate);
        ArgumentNullException.ThrowIfNull(onOccupancyUpdate);

        // Each subscriber has its own bounded inbox; like the broker's per-subscriber queue, a subscriber that
        // falls behind drops its oldest updates (the next status update supersedes them anyway).
        var inbox = Channel.CreateBounded<Delivery>(new BoundedChannelOptions(_subscriberCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        // Registered before the first await, so updates published after this call returns are received.
        lock (_subscribersGate)
        {
            _subscribers = [.. _subscribers, inbox];
        }

        try
        {
            await foreach (var delivery in inbox.Reader.ReadAllAsync(cancellationToken))
            {
                await DeliverAsync<object>(delivery, (message, token) => message switch
                {
                    ElevatorUpdateV1 update => onElevatorUpdate(update, token),
                    OccupancyUpdateV1 update => onOccupancyUpdate(update, token),
                    _ => throw new InvalidOperationException($"Unexpected update type {message.GetType().Name}."),
                }, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_subscribersGate)
            {
                _subscribers = [.. _subscribers.Where(subscriber => subscriber != inbox)];
            }
        }
    }

    private Task PublishUpdate(object update, Guid updateId, string type, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var activity = MessagingActivities.StartPublish(MessagingActivities.UpdatesDestination, updateId, type);
        var delivery = new Delivery(update, updateId, type, Activity.Current?.Context ?? default);
        foreach (var subscriber in Volatile.Read(ref _subscribers))
        {
            subscriber.Writer.TryWrite(delivery);
        }

        Log.Published(_logger, updateId, type);
        return Task.CompletedTask;
    }

    /// <summary>Hands <paramref name="delivery"/> to the handler; returns false if the consumer stopped first.</summary>
    private async Task<bool> DeliverAsync<T>(Delivery delivery, Func<T, CancellationToken, Task> handle, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var activity = MessagingActivities.StartProcess(delivery.Destination, delivery.MessageId, delivery.Type, delivery.Parent);
            try
            {
                await handle((T)delivery.Message, cancellationToken);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            // A failing handler must not stop delivery to the others; the failure is retried, then dropped.
            catch (Exception exception)
            {
                activity?.SetStatus(ActivityStatusCode.Error);
                if (attempt >= MaxDeliveryAttempts)
                {
                    Log.Dropped(_logger, exception, delivery.MessageId, delivery.Type, attempt);
                    return true;
                }

                Log.HandlerFailed(_logger, exception, delivery.MessageId, delivery.Type, attempt);
            }
        }
    }

    private sealed record Delivery(object Message, Guid MessageId, string Type, ActivityContext Parent)
    {
        public string Destination => Message is ElevatorCommandV1
            ? MessagingActivities.CommandsDestination
            : MessagingActivities.UpdatesDestination;
    }
}
