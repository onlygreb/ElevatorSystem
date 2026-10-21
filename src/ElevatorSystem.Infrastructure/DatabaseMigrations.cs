using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElevatorSystem.Infrastructure;

/// <summary>
/// Applies pending migrations on PostgreSQL. On a fresh database EF Core reads its history table before creating it and
/// logs that failure as an error; creating the table first, with EF's own script, keeps a first start free of errors
/// (ADR-0034). An advisory lock, one per schema, makes instances that start together take turns, so they never race to
/// create the same table.
/// </summary>
internal static class DatabaseMigrations
{
    public static async Task MigrateAsync(DbContext context, long lockKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock({0})", [lockKey], cancellationToken);
            try
            {
                await context.Database.ExecuteSqlRawAsync(context.GetService<IHistoryRepository>().GetCreateIfNotExistsScript(), cancellationToken);
                await context.Database.MigrateAsync(cancellationToken);
            }
            finally
            {
                // Released on this connection; closing it would release the lock too.
                await context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock({0})", [lockKey], CancellationToken.None);
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }
}
