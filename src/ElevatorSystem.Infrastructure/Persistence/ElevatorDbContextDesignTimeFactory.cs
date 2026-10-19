using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace ElevatorSystem.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> build the model without a host:
/// <c>dotnet ef migrations add &lt;Name&gt; --project src/ElevatorSystem.Infrastructure --output-dir Persistence/Migrations</c>.
/// Creating a migration never connects, so the connection string is a placeholder without credentials.
/// </summary>
internal sealed class ElevatorDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ElevatorDbContext>
{
    public ElevatorDbContext CreateDbContext(string[] args)
    {
        var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=elevator");
        var builder = new DbContextOptionsBuilder<ElevatorDbContext>();
        ElevatorDbContext.Configure(builder, dataSource);
        return new ElevatorDbContext(builder.Options);
    }
}
