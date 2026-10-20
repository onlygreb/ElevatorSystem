using ElevatorSystem.Application.Audit;
using ElevatorSystem.Infrastructure.Persistence;
using ElevatorSystem.Infrastructure.Staff;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ElevatorSystem.Persistence.Tests;

/// <summary>
/// The staff schema on PostgreSQL: its migration, next to the engine's, and the audit chain stored with every
/// field (time, bytes, nulls) coming back exactly, so it verifies after a round trip (ADR-0022, ADR-0023).
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class StaffDatabaseTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Migration_CreatesTheStaffSchema_BesideTheEngineSchema()
    {
        await using var engine = await postgres.CreateMigratedDatabaseAsync();
        var staff = Factory(engine.ConnectionString);

        await new StaffDatabaseInitializer(staff).InitializeAsync(TestContext.Current.CancellationToken);
        await new StaffDatabaseInitializer(staff).InitializeAsync(TestContext.Current.CancellationToken);

        var tables = await TablesAsync(engine.ConnectionString, StaffDbContext.Schema);
        tables.ShouldBe(
            ["__ef_migrations_history", "audit_entry", "data_protection_key", "staff_account", "staff_account_claim", "staff_account_login",
             "staff_account_role", "staff_account_token", "staff_role", "staff_role_claim"],
            ignoreOrder: true);
        (await TablesAsync(engine.ConnectionString, ElevatorDbContext.Schema)).ShouldContain("elevator_state");
    }

    [Fact]
    public async Task AuditChain_SurvivesTheRoundTrip_AndStillVerifies()
    {
        using var trail = await TrailAsync();
        await trail.Trail.RecordAsync(new AuditRecord(AuditActions.SignIn, string.Empty, AuditOutcomes.Failed, null, System.Net.IPAddress.Loopback), TestContext.Current.CancellationToken);
        var actor = Guid.CreateVersion7();
        await trail.Trail.RecordAsync(new AuditRecord(AuditActions.ServiceAction, "EmergencyStop command 1", AuditOutcomes.Requested, actor), TestContext.Current.CancellationToken);

        var result = await trail.Trail.VerifyAsync(TestContext.Current.CancellationToken);

        result.IsIntact.ShouldBeTrue();
        result.HeadSequence.ShouldBe(2);
        var newest = (await trail.Trail.ReadPageAsync(null, 10, TestContext.Current.CancellationToken))[0];
        newest.ActorId.ShouldBe(actor);
        newest.ClientPseudonym.ShouldBeNull();
    }

    [Fact]
    public async Task Append_OfATakenSequence_IsAConflict_NotAFork()
    {
        using var trail = await TrailAsync();
        var first = await trail.Trail.RecordAsync(new AuditRecord(AuditActions.SignOut, string.Empty, AuditOutcomes.Succeeded), TestContext.Current.CancellationToken);

        await Should.ThrowAsync<AuditSequenceConflictException>(() => trail.Store.AppendAsync(first, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Anonymisation_ErasesPersonalFields_AndTheDatabaseKeepsThemErased()
    {
        using var trail = await TrailAsync();
        var actor = Guid.CreateVersion7();
        await trail.Trail.RecordAsync(new AuditRecord(AuditActions.SignIn, string.Empty, AuditOutcomes.Succeeded, actor, System.Net.IPAddress.Loopback), TestContext.Current.CancellationToken);

        (await trail.Trail.AnonymiseActorAsync(actor, "dsr-1", TestContext.Current.CancellationToken)).ShouldBe(1);

        var entries = await trail.Store.ReadForwardAsync(0, 10, TestContext.Current.CancellationToken);
        entries[0].IsAnonymised.ShouldBeTrue();
        entries[0].ActorId.ShouldBeNull();
        entries[0].ClientPseudonym.ShouldBeNull();
        entries[0].Salt.IsEmpty.ShouldBeTrue();
        (await trail.Trail.VerifyAsync(TestContext.Current.CancellationToken)).IsIntact.ShouldBeTrue();
    }

    private async Task<TestTrail> TrailAsync()
    {
        var factory = Factory(await postgres.CreateEmptyDatabaseAsync());
        await new StaffDatabaseInitializer(factory).InitializeAsync(TestContext.Current.CancellationToken);
        var store = new EfAuditStore(factory);
        var pseudonymizer = new ClientPseudonymizer(Options.Create(new AuditOptions()));
        return new TestTrail(store, new AuditTrail(store, pseudonymizer, TimeProvider.System, NullLogger<AuditTrail>.Instance));
    }

    private static ContextFactory Factory(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<StaffDbContext>();
        StaffDbContext.ConfigureNpgsql(builder, connectionString);
        return new ContextFactory(builder.Options);
    }

    private static async Task<List<string>> TablesAsync(string connectionString, string schema)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = @schema", connection);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var tables = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private sealed record TestTrail(EfAuditStore Store, AuditTrail Trail) : IDisposable
    {
        public void Dispose() => Trail.Dispose();
    }

    private sealed class ContextFactory(DbContextOptions<StaffDbContext> options) : IDbContextFactory<StaffDbContext>
    {
        public StaffDbContext CreateDbContext() => new(options);
    }
}
