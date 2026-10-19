using ElevatorSystem.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ElevatorSystem.Persistence.Tests;

/// <summary>
/// One PostgreSQL container for the whole collection. Every test gets its own database, so tests
/// never see each other's rows or advisory locks.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17.6-alpine")
        .Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    /// <summary>Connection string with rights to create databases and terminate sessions.</summary>
    public string AdminConnectionString => _container.GetConnectionString();

    /// <summary>Creates a database with no tables and returns its connection string.</summary>
    public async Task<string> CreateEmptyDatabaseAsync()
    {
        var name = $"elevator_test_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(AdminConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            // CREATE DATABASE takes no parameters; the name is generated above, never input.
            await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        return new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = name }.ConnectionString;
    }

    /// <summary>Creates a database with every migration applied.</summary>
    public async Task<TestDatabase> CreateMigratedDatabaseAsync()
    {
        var database = new TestDatabase(await CreateEmptyDatabaseAsync());
        await new ElevatorDatabaseMigrator(database.ContextFactory).MigrateAsync(TestContext.Current.CancellationToken);
        return database;
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "PostgreSQL";
}

/// <summary>One test's database: its connections and a context factory, as the DI extension would build them.</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    public TestDatabase(string connectionString)
    {
        ConnectionString = connectionString;
        Database = new ElevatorDatabase(connectionString);
        var builder = new DbContextOptionsBuilder<ElevatorDbContext>();
        ElevatorDbContext.Configure(builder, Database.DataSource);
        ContextFactory = new Factory(builder.Options);
    }

    public string ConnectionString { get; }

    public ElevatorDatabase Database { get; }

    public IDbContextFactory<ElevatorDbContext> ContextFactory { get; }

    public ValueTask DisposeAsync() => Database.DisposeAsync();

    private sealed class Factory(DbContextOptions<ElevatorDbContext> options) : IDbContextFactory<ElevatorDbContext>
    {
        public ElevatorDbContext CreateDbContext() => new(options);
    }
}
