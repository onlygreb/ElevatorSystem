using System.Collections.Concurrent;
using ElevatorSystem.Contracts;

namespace ElevatorSystem.Api.Staff;

/// <summary>
/// Makes a service action with an <c>Idempotency-Key</c> happen at most once per account and key (threat
/// T-API-05, OQ-15): a retry gets the first answer back instead of a second command and a second audit entry.
/// Only acceptances are remembered, so a request refused because the broker was down can be retried with the
/// same key. Keys live in memory for <see cref="Retention"/>; the API runs as one instance (ADR-0021).
/// </summary>
internal sealed class ServiceActionIdempotency(TimeProvider timeProvider)
{
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<(Guid Account, Guid Key), Slot> _slots = new();

    public enum Outcome
    {
        /// <summary>The action ran now; its result is in <see cref="Result.Response"/> when it was accepted.</summary>
        Executed,

        /// <summary>An earlier request with this key was accepted; <see cref="Result.Response"/> is its answer.</summary>
        Replayed,

        /// <summary>The key was already used for a different action.</summary>
        KeyReused,
    }

    public readonly record struct Result(Outcome Outcome, ServiceActionAcceptedResponse? Response);

    /// <summary>Runs <paramref name="execute"/> unless this account already used <paramref name="key"/>.</summary>
    /// <param name="account">The signed-in account; keys are never shared between accounts.</param>
    /// <param name="key">The client's <c>Idempotency-Key</c>.</param>
    /// <param name="action">The requested action; reusing a key for another action is refused.</param>
    /// <param name="execute">Runs the action; returns its acceptance, or <see langword="null"/> when it was refused.</param>
    /// <param name="cancellationToken">Stops waiting for a concurrent request with the same key.</param>
    public async Task<Result> RunAsync(
        Guid account, Guid key, ServiceAction action, Func<Task<ServiceActionAcceptedResponse?>> execute, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execute);
        Prune();

        var slot = _slots.GetOrAdd((account, key), _ => new Slot(action, timeProvider.GetUtcNow()));
        if (slot.Action != action)
        {
            return new Result(Outcome.KeyReused, null);
        }

        // Concurrent requests with one key run one after the other, so the second sees the first's answer.
        await slot.Gate.WaitAsync(cancellationToken);
        try
        {
            if (slot.Accepted is { } earlier)
            {
                return new Result(Outcome.Replayed, earlier);
            }

            slot.Accepted = await execute();
            return new Result(Outcome.Executed, slot.Accepted);
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    private void Prune()
    {
        var cutoff = timeProvider.GetUtcNow() - Retention;
        foreach (var (id, slot) in _slots)
        {
            if (slot.CreatedAt < cutoff)
            {
                _slots.TryRemove(new KeyValuePair<(Guid, Guid), Slot>(id, slot));
            }
        }
    }

    private sealed class Slot(ServiceAction action, DateTimeOffset createdAt)
    {
        public ServiceAction Action { get; } = action;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        // Never disposed: a pruned slot may still be awaited by a request in flight, and an undisposed
        // SemaphoreSlim without a wait handle holds no unmanaged resources.
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public ServiceActionAcceptedResponse? Accepted { get; set; }
    }
}
