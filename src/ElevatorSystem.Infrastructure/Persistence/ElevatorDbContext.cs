using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ElevatorSystem.Infrastructure.Persistence;

/// <summary>
/// The engine's database (ADR-0007, ADR-0019): the elevator snapshot, its pending calls and the
/// processed-command log, all in the <c>elevator</c> schema. Contexts are short-lived and created
/// through <see cref="IDbContextFactory{TContext}"/>.
/// </summary>
public sealed class ElevatorDbContext(DbContextOptions<ElevatorDbContext> options) : DbContext(options)
{
    public const string Schema = "elevator";

    internal DbSet<ElevatorStateRecord> ElevatorStates => Set<ElevatorStateRecord>();

    internal DbSet<PendingCallRecord> PendingCalls => Set<PendingCallRecord>();

    internal DbSet<ProcessedCommandRecord> ProcessedCommands => Set<ProcessedCommandRecord>();

    /// <summary>Applies the provider settings every context uses, at run time and at design time.</summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dataSource);

        return builder.UseNpgsql(dataSource, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", Schema));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<ElevatorStateRecord>(state =>
        {
            state.ToTable("elevator_state", table =>
            {
                table.HasCheckConstraint("ck_elevator_state_state", "state IN ('idle', 'moving_up', 'moving_down', 'door_open', 'out_of_service')");
                table.HasCheckConstraint("ck_elevator_state_travel_direction", "travel_direction IS NULL OR travel_direction IN ('up', 'down')");
                table.HasCheckConstraint(
                    "ck_elevator_state_out_of_service_reason",
                    "(state = 'out_of_service' AND out_of_service_reason IN ('maintenance', 'fault', 'emergency_stop'))"
                    + " OR (state <> 'out_of_service' AND out_of_service_reason IS NULL)");
            });
            state.HasKey(record => record.Id).HasName("pk_elevator_state");
            state.Property(record => record.Id).HasColumnName("id").ValueGeneratedNever();
            state.Property(record => record.CurrentFloor).HasColumnName("current_floor");
            state.Property(record => record.State).HasColumnName("state").HasMaxLength(16);
            state.Property(record => record.TravelDirection).HasColumnName("travel_direction").HasMaxLength(8);
            state.Property(record => record.OutOfServiceReason).HasColumnName("out_of_service_reason").HasMaxLength(16);
            state.Property(record => record.Version).HasColumnName("version");
            state.Property(record => record.TakenAt).HasColumnName("taken_at");
            state.HasMany(record => record.PendingCalls)
                .WithOne()
                .HasForeignKey(call => call.ElevatorId)
                .HasConstraintName("fk_elevator_pending_call_elevator_state")
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PendingCallRecord>(call =>
        {
            call.ToTable("elevator_pending_call", table =>
            {
                table.HasCheckConstraint(
                    "ck_elevator_pending_call_kind",
                    "(kind = 'hall' AND direction IN ('up', 'down')) OR (kind = 'car' AND direction IS NULL)");
                table.HasCheckConstraint("ck_elevator_pending_call_request_count", "request_count > 0");
            });
            call.HasKey(record => new { record.ElevatorId, record.Position }).HasName("pk_elevator_pending_call");
            call.Property(record => record.ElevatorId).HasColumnName("elevator_id");
            call.Property(record => record.Position).HasColumnName("position").ValueGeneratedNever();
            call.Property(record => record.Kind).HasColumnName("kind").HasMaxLength(8);
            call.Property(record => record.Floor).HasColumnName("floor");
            call.Property(record => record.Direction).HasColumnName("direction").HasMaxLength(8);
            call.Property(record => record.FirstRequestId).HasColumnName("first_request_id");
            call.Property(record => record.FirstRequestedAt).HasColumnName("first_requested_at");
            call.Property(record => record.RequestCount).HasColumnName("request_count");
        });

        modelBuilder.Entity<ProcessedCommandRecord>(command =>
        {
            command.ToTable("processed_command");
            command.HasKey(record => record.CommandId).HasName("pk_processed_command");
            command.Property(record => record.CommandId).HasColumnName("command_id").ValueGeneratedNever();
            command.Property(record => record.ProcessedAt).HasColumnName("processed_at");
            command.HasIndex(record => record.ProcessedAt).HasDatabaseName("ix_processed_command_processed_at");
        });
    }
}
