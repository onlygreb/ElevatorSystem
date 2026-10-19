using ElevatorSystem.Application.Ports;
using ElevatorSystem.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace ElevatorSystem.Infrastructure.Persistence;

/// <summary>
/// Keeps the latest elevator snapshot in PostgreSQL (ADR-0007). A save is a single conditional
/// upsert on the elevator's row, so a stale snapshot never overwrites a newer one, however the
/// saves interleave: the row lock taken by the upsert also serialises the rewrite of pending calls.
/// </summary>
public sealed class PostgresElevatorStateStore(IDbContextFactory<ElevatorDbContext> contextFactory, ILogger<PostgresElevatorStateStore>? logger = null)
    : IElevatorStateStore
{
    /// <summary>The single elevator's row. A second car would get its own ID.</summary>
    internal const short ElevatorId = 1;

    private readonly IDbContextFactory<ElevatorDbContext> _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    private readonly ILogger _logger = logger ?? NullLogger<PostgresElevatorStateStore>.Instance;

    public async Task SaveAsync(ElevatorSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var record = SnapshotMapping.ToRecord(ElevatorId, snapshot);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var written = await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO elevator.elevator_state (id, current_floor, state, travel_direction, out_of_service_reason, version, taken_at)
            VALUES ({record.Id}, {record.CurrentFloor}, {record.State}, {Text(record.TravelDirection)}, {Text(record.OutOfServiceReason)}, {record.Version}, {record.TakenAt})
            ON CONFLICT (id) DO UPDATE SET
                current_floor = EXCLUDED.current_floor,
                state = EXCLUDED.state,
                travel_direction = EXCLUDED.travel_direction,
                out_of_service_reason = EXCLUDED.out_of_service_reason,
                version = EXCLUDED.version,
                taken_at = EXCLUDED.taken_at
            WHERE elevator_state.version < EXCLUDED.version
            """,
            cancellationToken);

        if (written == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            PersistenceLog.StaleSnapshotIgnored(_logger, snapshot.Version);
            return;
        }

        await context.PendingCalls.Where(call => call.ElevatorId == ElevatorId).ExecuteDeleteAsync(cancellationToken);
        context.PendingCalls.AddRange(record.PendingCalls);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ElevatorSnapshot?> LoadAsync(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var record = await context.ElevatorStates
            .AsNoTracking()
            .Include(state => state.PendingCalls)
            .SingleOrDefaultAsync(state => state.Id == ElevatorId, cancellationToken);

        return record is null ? null : SnapshotMapping.ToSnapshot(record);
    }

    // A typed parameter, so a null value still has a type PostgreSQL can check against the column.
    private static NpgsqlParameter Text(string? value) => new() { NpgsqlDbType = NpgsqlDbType.Varchar, Value = value ?? (object)DBNull.Value };
}
