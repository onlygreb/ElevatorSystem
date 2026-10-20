using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ElevatorSystem.Infrastructure.Staff.Migrations
{
    /// <inheritdoc />
    public partial class InitialStaff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "staff");

            migrationBuilder.CreateTable(
                name: "audit_entry",
                schema: "staff",
                columns: table => new
                {
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    target = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    client_pseudonym = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    salt = table.Column<byte[]>(type: "bytea", nullable: true),
                    personal_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    entry_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    anonymised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    anonymisation_reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_entry", x => x.sequence);
                    table.CheckConstraint("ck_audit_entry_anonymised", "(anonymised_at IS NULL AND anonymisation_reference IS NULL) OR (anonymised_at IS NOT NULL AND anonymisation_reference IS NOT NULL AND actor_id IS NULL AND client_pseudonym IS NULL AND salt IS NULL)");
                    table.CheckConstraint("ck_audit_entry_sequence", "sequence > 0");
                });

            migrationBuilder.CreateTable(
                name: "data_protection_key",
                schema: "staff",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    friendly_name = table.Column<string>(type: "text", nullable: true),
                    xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_protection_key", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "staff_account",
                schema: "staff",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    email_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    security_stamp = table.Column<string>(type: "text", nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true),
                    phone_number = table.Column<string>(type: "text", nullable: true),
                    phone_number_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    two_factor_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    lockout_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lockout_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    access_failed_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_account", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "staff_role",
                schema: "staff",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_role", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "staff_account_claim",
                schema: "staff",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_type = table.Column<string>(type: "text", nullable: true),
                    claim_value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_account_claim", x => x.id);
                    table.ForeignKey(
                        name: "FK_staff_account_claim_staff_account_user_id",
                        column: x => x.user_id,
                        principalSchema: "staff",
                        principalTable: "staff_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "staff_account_login",
                schema: "staff",
                columns: table => new
                {
                    login_provider = table.Column<string>(type: "text", nullable: false),
                    provider_key = table.Column<string>(type: "text", nullable: false),
                    provider_display_name = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_account_login", x => new { x.login_provider, x.provider_key });
                    table.ForeignKey(
                        name: "FK_staff_account_login_staff_account_user_id",
                        column: x => x.user_id,
                        principalSchema: "staff",
                        principalTable: "staff_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "staff_account_token",
                schema: "staff",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    login_provider = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_account_token", x => new { x.user_id, x.login_provider, x.name });
                    table.ForeignKey(
                        name: "FK_staff_account_token_staff_account_user_id",
                        column: x => x.user_id,
                        principalSchema: "staff",
                        principalTable: "staff_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "staff_account_role",
                schema: "staff",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_account_role", x => new { x.user_id, x.role_id });
                    table.ForeignKey(
                        name: "FK_staff_account_role_staff_account_user_id",
                        column: x => x.user_id,
                        principalSchema: "staff",
                        principalTable: "staff_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_staff_account_role_staff_role_role_id",
                        column: x => x.role_id,
                        principalSchema: "staff",
                        principalTable: "staff_role",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "staff_role_claim",
                schema: "staff",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_type = table.Column<string>(type: "text", nullable: true),
                    claim_value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_role_claim", x => x.id);
                    table.ForeignKey(
                        name: "FK_staff_role_claim_staff_role_role_id",
                        column: x => x.role_id,
                        principalSchema: "staff",
                        principalTable: "staff_role",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entry_actor_id",
                schema: "staff",
                table: "audit_entry",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                schema: "staff",
                table: "staff_account",
                column: "normalized_email");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                schema: "staff",
                table: "staff_account",
                column: "normalized_user_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_staff_account_claim_user_id",
                schema: "staff",
                table: "staff_account_claim",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_staff_account_login_user_id",
                schema: "staff",
                table: "staff_account_login",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_staff_account_role_role_id",
                schema: "staff",
                table: "staff_account_role",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                schema: "staff",
                table: "staff_role",
                column: "normalized_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_staff_role_claim_role_id",
                schema: "staff",
                table: "staff_role_claim",
                column: "role_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_entry",
                schema: "staff");

            migrationBuilder.DropTable(
                name: "data_protection_key",
                schema: "staff");

            migrationBuilder.DropTable(
                name: "staff_account_claim",
                schema: "staff");

            migrationBuilder.DropTable(
                name: "staff_account_login",
                schema: "staff");

            migrationBuilder.DropTable(
                name: "staff_account_role",
                schema: "staff");

            migrationBuilder.DropTable(
                name: "staff_account_token",
                schema: "staff");

            migrationBuilder.DropTable(
                name: "staff_role_claim",
                schema: "staff");

            migrationBuilder.DropTable(
                name: "staff_account",
                schema: "staff");

            migrationBuilder.DropTable(
                name: "staff_role",
                schema: "staff");
        }
    }
}
