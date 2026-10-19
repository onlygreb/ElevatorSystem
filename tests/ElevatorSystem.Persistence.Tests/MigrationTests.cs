using ElevatorSystem.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ElevatorSystem.Persistence.Tests;

[Collection(PostgresTestGroup.Name)]
public sealed class MigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task MigrateAsync_EmptyDatabase_AppliesEveryMigrationAndCreatesTheTables()
    {
        await using var database = new TestDatabase(await postgres.CreateEmptyDatabaseAsync());

        await new ElevatorDatabaseMigrator(database.ContextFactory).MigrateAsync(TestContext.Current.CancellationToken);

        await using var context = await database.ContextFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        (await context.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)).ShouldBe(context.Database.GetMigrations());
        (await TablesAsync(database)).ShouldBe(["__ef_migrations_history", "elevator_pending_call", "elevator_state", "processed_command"]);
    }

    [Fact]
    public async Task MigrateAsync_RunTwiceAndConcurrently_IsIdempotent()
    {
        await using var database = new TestDatabase(await postgres.CreateEmptyDatabaseAsync());
        var migrator = new ElevatorDatabaseMigrator(database.ContextFactory);

        await Task.WhenAll(
            migrator.MigrateAsync(TestContext.Current.CancellationToken),
            migrator.MigrateAsync(TestContext.Current.CancellationToken));
        await migrator.MigrateAsync(TestContext.Current.CancellationToken);

        (await TablesAsync(database)).Count.ShouldBe(4);
    }

    [Fact]
    public async Task Model_MatchesTheLatestMigration()
    {
        await using var database = await postgres.CreateMigratedDatabaseAsync();
        await using var context = await database.ContextFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);

        context.Database.HasPendingModelChanges().ShouldBeFalse(
            "the model changed without a migration: run dotnet ef migrations add <Name> --project src/ElevatorSystem.Infrastructure --output-dir Persistence/Migrations");
    }

    private static async Task<List<string>> TablesAsync(TestDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = $1 ORDER BY table_name",
            connection)
        {
            Parameters = { new NpgsqlParameter { Value = ElevatorDbContext.Schema } },
        };

        List<string> tables = [];
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }
}
