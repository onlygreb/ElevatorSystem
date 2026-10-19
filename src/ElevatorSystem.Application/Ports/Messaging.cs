using ElevatorSystem.Contracts;

namespace ElevatorSystem.Application.Ports;

// Messaging ports (ADR-0003, messaging skill). Infrastructure implements them on RabbitMQ;
// in-memory implementations serve the console demo, tests and single-process hosting.

/// <summary>Sends passenger calls from the API to the engine.</summary>
public interface IElevatorCommandPublisher
{
    /// <summary>
    /// Publishes <paramref name="command"/> and completes only once the broker has confirmed it is stored.
    /// </summary>
    /// <exception cref="MessagingUnavailableException">The command could not be confirmed; the caller must not report it as accepted.</exception>
    Task PublishAsync(ElevatorCommandV1 command, CancellationToken cancellationToken);
}

/// <summary>Delivers passenger calls to the engine.</summary>
public interface IElevatorCommandConsumer
{
    /// <summary>
    /// Calls <paramref name="handle"/> for each command until <paramref name="cancellationToken"/> is cancelled.
    /// A command is acknowledged only after <paramref name="handle"/> completes; if it throws, the command is
    /// retried a bounded number of times and then dead-lettered. Delivery is at least once.
    /// </summary>
    Task ConsumeAsync(Func<ElevatorCommandV1, CancellationToken, Task> handle, CancellationToken cancellationToken);
}

/// <summary>Publishes the engine's updates to every subscriber (the API's real-time hub, and later the audit log).</summary>
public interface IElevatorUpdatePublisher
{
    Task PublishAsync(ElevatorUpdateV1 update, CancellationToken cancellationToken);

    Task PublishAsync(OccupancyUpdateV1 update, CancellationToken cancellationToken);
}

/// <summary>Receives the engine's updates.</summary>
public interface IElevatorUpdateSubscriber
{
    /// <summary>
    /// Calls the matching handler for each update until <paramref name="cancellationToken"/> is cancelled.
    /// Updates may be redelivered; use their <c>UpdateId</c> to drop duplicates.
    /// </summary>
    Task SubscribeAsync(
        Func<ElevatorUpdateV1, CancellationToken, Task> onElevatorUpdate,
        Func<OccupancyUpdateV1, CancellationToken, Task> onOccupancyUpdate,
        CancellationToken cancellationToken);
}

/// <summary>The message broker could not be reached or did not confirm a message in time.</summary>
public sealed class MessagingUnavailableException : Exception
{
    public MessagingUnavailableException()
    {
    }

    public MessagingUnavailableException(string message)
        : base(message)
    {
    }

    public MessagingUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
