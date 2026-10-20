namespace ElevatorSystem.Application.Hosting;

/// <summary>Settings of the engine around the controller, bound from the <c>Engine</c> configuration section.</summary>
public sealed record ElevatorEngineOptions
{
    public const string SectionName = "Engine";

    /// <summary>How many updates may wait for the broker before the oldest is dropped (ADR-0015).</summary>
    public int UpdateBufferCapacity { get; init; } = 1024;

    /// <summary>
    /// How often the leader re-sends its latest updates when nothing changed, so a subscriber that joined
    /// late (a restarted API) or missed updates during a broker outage learns the current state.
    /// Subscribers that already hold an update drop the copy by its id.
    /// </summary>
    public TimeSpan StatusRefreshInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The longest a single store operation (load, save, deduplication) may take before it counts as failed.</summary>
    public TimeSpan StoreTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long to wait before retrying a store operation that failed.</summary>
    public TimeSpan StoreRetryInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a graceful stop may spend saving the last state and publishing the last updates.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Throws if any setting is out of range.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(UpdateBufferCapacity, 1, nameof(UpdateBufferCapacity));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(StatusRefreshInterval, TimeSpan.Zero, nameof(StatusRefreshInterval));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(StoreTimeout, TimeSpan.Zero, nameof(StoreTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(StoreRetryInterval, TimeSpan.Zero, nameof(StoreRetryInterval));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ShutdownTimeout, TimeSpan.Zero, nameof(ShutdownTimeout));
    }
}
