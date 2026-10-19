using ElevatorSystem.Application.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ElevatorSystem.Infrastructure.Persistence;

/// <summary>
/// Records processed command IDs in PostgreSQL. The command ID is the primary key and a duplicate
/// insert is ignored by the database, so of any number of concurrent attempts with one ID exactly
/// one succeeds, without exceptions. Entries are purged after
/// <see cref="PersistenceOptions.ProcessedCommandRetention"/>.
/// </summary>
public sealed class PostgresProcessedCommandStore(
    IDbContextFactory<ElevatorDbContext> contextFactory,
    IOptions<PersistenceOptions> options,
    TimeProvider timeProvider) : IProcessedCommandStore
{
    /// <summary>Rows deleted per statement, so a large purge never holds long locks.</summary>
    internal const int PurgeBatchSize = 1000;

    private readonly IDbContextFactory<ElevatorDbContext> _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    private readonly IOptions<PersistenceOptions> _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<bool> TryMarkProcessedAsync(Guid commandId, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var processedAt = _timeProvider.GetUtcNow();

        var inserted = await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO elevator.processed_command (command_id, processed_at)
            VALUES ({commandId}, {processedAt})
            ON CONFLICT (command_id) DO NOTHING
            """,
            cancellationToken);

        return inserted == 1;
    }

    /// <summary>
    /// Deletes every entry recorded more than the retention period ago, in batches, and returns how
    /// many were deleted. Safe to run from several instances at once and idempotent.
    /// </summary>
    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        var cutoff = _timeProvider.GetUtcNow() - _options.Value.ProcessedCommandRetention;
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var total = 0;
        int deleted;
        do
        {
            deleted = await context.Database.ExecuteSqlAsync(
                $"""
                DELETE FROM elevator.processed_command
                WHERE command_id IN (
                    SELECT command_id FROM elevator.processed_command
                    WHERE processed_at < {cutoff}
                    ORDER BY processed_at
                    LIMIT {PurgeBatchSize}
                    FOR UPDATE SKIP LOCKED)
                """,
                cancellationToken);
            total += deleted;
        }
        while (deleted == PurgeBatchSize);

        return total;
    }
}
