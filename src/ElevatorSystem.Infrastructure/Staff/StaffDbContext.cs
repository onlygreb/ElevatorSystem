using System.Text;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ElevatorSystem.Infrastructure.Staff;

/// <summary>A staff account (ADR-0022): the e-mail is the user name. Sensitive data (security skill).</summary>
public sealed class StaffUser : IdentityUser<Guid>
{
}

/// <summary>
/// The API's database (ADR-0022, ADR-0023): staff accounts and roles (ASP.NET Core Identity), the audit chain
/// and the Data Protection key ring, in the <c>staff</c> schema of PostgreSQL. In single-process mode the same
/// model lives in an in-memory SQLite database, which has no schemas.
/// </summary>
public sealed class StaffDbContext(DbContextOptions<StaffDbContext> options)
    : IdentityDbContext<StaffUser, IdentityRole<Guid>, Guid>(options), IDataProtectionKeyContext
{
    public const string Schema = "staff";
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

    internal DbSet<AuditEntryRecord> AuditEntries => Set<AuditEntryRecord>();

    /// <summary>PostgreSQL settings, used at run time and at design time.</summary>
    public static DbContextOptionsBuilder ConfigureNpgsql(DbContextOptionsBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return builder.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(MigrationsHistoryTable, Schema));
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        if (Database.IsNpgsql())
        {
            builder.HasDefaultSchema(Schema);
        }

        builder.Entity<StaffUser>().ToTable("staff_account");
        builder.Entity<IdentityRole<Guid>>().ToTable("staff_role");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("staff_account_role");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("staff_account_claim");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("staff_account_login");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("staff_account_token");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("staff_role_claim");
        builder.Entity<DataProtectionKey>().ToTable("data_protection_key");

        builder.Entity<AuditEntryRecord>(entry =>
        {
            entry.ToTable("audit_entry", table =>
            {
                table.HasCheckConstraint("ck_audit_entry_sequence", "sequence > 0");
                table.HasCheckConstraint(
                    "ck_audit_entry_anonymised",
                    "(anonymised_at IS NULL AND anonymisation_reference IS NULL)"
                    + " OR (anonymised_at IS NOT NULL AND anonymisation_reference IS NOT NULL AND actor_id IS NULL AND client_pseudonym IS NULL AND salt IS NULL)");
            });
            entry.HasKey(record => record.Sequence).HasName("pk_audit_entry");
            entry.Property(record => record.Sequence).ValueGeneratedNever();
            entry.Property(record => record.Action).HasMaxLength(64);
            entry.Property(record => record.Target).HasMaxLength(200);
            entry.Property(record => record.Outcome).HasMaxLength(32);
            entry.Property(record => record.ClientPseudonym).HasMaxLength(64);
            entry.Property(record => record.AnonymisationReference).HasMaxLength(64);
            entry.HasIndex(record => record.ActorId).HasDatabaseName("ix_audit_entry_actor_id");
        });

        // Lower-case snake_case columns, as in the engine's schema, so SQL against either reads the same way.
        foreach (var entity in builder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(SnakeCase(property.Name));
            }
        }
    }

    internal static string SnakeCase(string name)
    {
        var result = new StringBuilder(name.Length + 8);
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (char.IsUpper(character) && index > 0 && (char.IsLower(name[index - 1]) || (index + 1 < name.Length && char.IsLower(name[index + 1]))))
            {
                result.Append('_');
            }

            result.Append(char.ToLowerInvariant(character));
        }

        return result.ToString();
    }
}

/// <summary>A row of the audit chain; see <see cref="Application.Audit.AuditEntry"/> for the meaning of each field.</summary>
internal sealed class AuditEntryRecord
{
    public long Sequence { get; set; }

    public DateTimeOffset RecordedAt { get; set; }

    public string Action { get; set; } = "";

    public string Target { get; set; } = "";

    public string Outcome { get; set; } = "";

    public Guid? ActorId { get; set; }

    public string? ClientPseudonym { get; set; }

    public byte[]? Salt { get; set; }

    public byte[] PersonalDigest { get; set; } = [];

    public byte[] EntryHash { get; set; } = [];

    public DateTimeOffset? AnonymisedAt { get; set; }

    public string? AnonymisationReference { get; set; }
}
