using Microsoft.EntityFrameworkCore;

namespace ElevatorSystem.Infrastructure.Persistence;

/// <summary>
/// Applies pending migrations. Hosts call it at start-up, before competing for leadership. Engines
/// that start together are safe: they take turns under an advisory lock, so the second waits and then
/// finds nothing to do.
/// </summary>
public sealed class ElevatorDatabaseMigrator(IDbContextFactory<ElevatorDbContext> contextFactory)
{
    /// <summary>The advisory lock that serialises migrations of the elevator schema (any key distinct from the others in use).</summary>
    public const long MigrationLockKey = 7_300_000_002;

    private readonly IDbContextFactory<ElevatorDbContext> _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await DatabaseMigrations.MigrateAsync(context, MigrationLockKey, cancellationToken);
    }
}
