using ElevatorSystem.Application.Ports;
using ElevatorSystem.Domain;

namespace ElevatorSystem.Application.InMemory.Persistence;

/// <summary>
/// Keeps the latest snapshot in memory, for the console demo, tests and single-process hosting.
/// A save with a <c>Version</c> no higher than the stored one is ignored, as in the database store.
/// </summary>
public sealed class InMemoryElevatorStateStore : IElevatorStateStore
{
    private readonly Lock _gate = new();
    private ElevatorSnapshot? _latest;

    public Task SaveAsync(ElevatorSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_latest is null || snapshot.Version > _latest.Version)
            {
                _latest = snapshot;
            }
        }

        return Task.CompletedTask;
    }

    public Task<ElevatorSnapshot?> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult(_latest);
        }
    }
}
