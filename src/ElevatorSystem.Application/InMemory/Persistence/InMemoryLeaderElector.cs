using ElevatorSystem.Application.Ports;

namespace ElevatorSystem.Application.InMemory.Persistence;

/// <summary>
/// Grants leadership to one caller at a time within the process. Share one instance between the
/// engines that compete: <see cref="AcquireAsync"/> waits until the current lease is disposed or
/// revoked. <see cref="RevokeLeadership"/> simulates losing leadership, for failover tests.
/// </summary>
public sealed class InMemoryLeaderElector : ILeaderElector, IDisposable
{
    private readonly SemaphoreSlim _leadership = new(1, 1);
    private readonly Lock _gate = new();
    private Lease? _current;

    /// <summary>Whether some caller holds leadership right now.</summary>
    public bool IsHeld
    {
        get
        {
            lock (_gate)
            {
                return _current is not null;
            }
        }
    }

    public async Task<ILeadershipLease> AcquireAsync(CancellationToken cancellationToken)
    {
        await _leadership.WaitAsync(cancellationToken);

        var lease = new Lease(this);
        lock (_gate)
        {
            _current = lease;
        }

        return lease;
    }

    /// <summary>Takes leadership away from the current holder, as a dropped database connection would. Does nothing when nobody leads.</summary>
    public void RevokeLeadership()
    {
        Lease? current;
        lock (_gate)
        {
            current = _current;
        }

        current?.Release();
    }

    public void Dispose() => _leadership.Dispose();

    private void OnReleased(Lease lease)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, lease))
            {
                _current = null;
            }
        }

        _leadership.Release();
    }

    private sealed class Lease : ILeadershipLease
    {
        // Never disposed: it has no timer, and Lost must stay readable after the lease is released.
        private readonly CancellationTokenSource _lost = new();
        private readonly InMemoryLeaderElector _owner;
        private int _released;

        public Lease(InMemoryLeaderElector owner)
        {
            _owner = owner;
            Lost = _lost.Token;
        }

        public CancellationToken Lost { get; }

        public ValueTask DisposeAsync()
        {
            Release();
            return ValueTask.CompletedTask;
        }

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 1)
            {
                return;
            }

            // Cancel before handing leadership on, so the old holder is told to stop first.
            _lost.Cancel();
            _owner.OnReleased(this);
        }
    }
}
