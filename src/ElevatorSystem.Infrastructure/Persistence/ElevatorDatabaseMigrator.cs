using Microsoft.EntityFrameworkCore;

namespace ElevatorSystem.Infrastructure.Persistence;

/// <summary>
/// Applies pending migrations. Hosts call it at start-up, before competing for leadership. Engines
/// that start together are safe: EF Core holds a database lock while it migrates, so the second
/// waits and then finds nothing to do.
/// </summary>
public sealed class ElevatorDatabaseMigrator(IDbContextFactory<ElevatorDbContext> contextFactory)
{
    private readonly IDbContextFactory<ElevatorDbContext> _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.MigrateAsync(cancellationToken);
    }
}
