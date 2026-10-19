using ElevatorSystem.Domain;

namespace ElevatorSystem.Application.Ports;

// Persistence ports (ADR-0007). Infrastructure implements them on PostgreSQL; in-memory
// implementations serve the console demo, tests and single-process hosting.

/// <summary>Keeps the latest elevator snapshot so the engine can resume after a restart.</summary>
public interface IElevatorStateStore
{
    /// <summary>Saves <paramref name="snapshot"/> unless a snapshot with a higher <c>Version</c> is already stored.</summary>
    Task SaveAsync(ElevatorSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>The latest saved snapshot, or <see langword="null"/> when none was ever saved.</summary>
    Task<ElevatorSnapshot?> LoadAsync(CancellationToken cancellationToken);
}

/// <summary>Remembers which commands were applied, so a redelivered command is applied only once.</summary>
public interface IProcessedCommandStore
{
    /// <summary>
    /// Records <paramref name="commandId"/> as processed. Returns <see langword="false"/> when it was
    /// already recorded, in which case the command must not be applied again.
    /// </summary>
    Task<bool> TryMarkProcessedAsync(Guid commandId, CancellationToken cancellationToken);
}

/// <summary>Ensures that only one engine instance drives the elevator at a time.</summary>
public interface ILeaderElector
{
    /// <summary>Waits until this instance becomes the leader, and returns the lease that proves it.</summary>
    Task<ILeadershipLease> AcquireAsync(CancellationToken cancellationToken);
}

/// <summary>Leadership held by this instance. Disposing it releases leadership.</summary>
public interface ILeadershipLease : IAsyncDisposable
{
    /// <summary>Cancelled the moment leadership is lost (for example the database connection dropped); stop driving the car at once.</summary>
    CancellationToken Lost { get; }
}
