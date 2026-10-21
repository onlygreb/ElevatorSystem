using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ElevatorSystem.Infrastructure.Staff;

/// <summary>
/// Brings the staff database up to date at start-up: PostgreSQL through migrations (EF Core holds a database
/// lock while it migrates), the single-process SQLite database by creating the schema from the model.
/// </summary>
public sealed class StaffDatabaseInitializer(IDbContextFactory<StaffDbContext> contextFactory)
{
    /// <summary>The advisory lock that serialises migrations of the staff schema (any key distinct from the others in use).</summary>
    public const long MigrationLockKey = 7_300_000_001;

    private readonly IDbContextFactory<StaffDbContext> _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        if (context.Database.IsNpgsql())
        {
            await DatabaseMigrations.MigrateAsync(context, MigrationLockKey, cancellationToken);
        }
        else
        {
            await context.Database.EnsureCreatedAsync(cancellationToken);
        }
    }
}

/// <summary>
/// Lets <c>dotnet ef</c> build the model without a host:
/// <c>dotnet ef migrations add &lt;Name&gt; --project src/ElevatorSystem.Infrastructure --context StaffDbContext --output-dir Staff/Migrations</c>.
/// Creating a migration never connects, so the connection string is a placeholder without credentials.
/// </summary>
internal sealed class StaffDbContextDesignTimeFactory : IDesignTimeDbContextFactory<StaffDbContext>
{
    public StaffDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<StaffDbContext>();
        StaffDbContext.ConfigureNpgsql(builder, "Host=localhost;Database=elevator");
        return new StaffDbContext(builder.Options);
    }
}
