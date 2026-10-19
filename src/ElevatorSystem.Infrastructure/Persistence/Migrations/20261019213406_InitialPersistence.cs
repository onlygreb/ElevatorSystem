using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElevatorSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "elevator");

            migrationBuilder.CreateTable(
                name: "elevator_state",
                schema: "elevator",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    current_floor = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    travel_direction = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    out_of_service_reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    taken_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_elevator_state", x => x.id);
                    table.CheckConstraint("ck_elevator_state_out_of_service_reason", "(state = 'out_of_service' AND out_of_service_reason IN ('maintenance', 'fault', 'emergency_stop')) OR (state <> 'out_of_service' AND out_of_service_reason IS NULL)");
                    table.CheckConstraint("ck_elevator_state_state", "state IN ('idle', 'moving_up', 'moving_down', 'door_open', 'out_of_service')");
                    table.CheckConstraint("ck_elevator_state_travel_direction", "travel_direction IS NULL OR travel_direction IN ('up', 'down')");
                });

            migrationBuilder.CreateTable(
                name: "processed_command",
                schema: "elevator",
                columns: table => new
                {
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processed_command", x => x.command_id);
                });

            migrationBuilder.CreateTable(
                name: "elevator_pending_call",
                schema: "elevator",
                columns: table => new
                {
                    elevator_id = table.Column<short>(type: "smallint", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    floor = table.Column<int>(type: "integer", nullable: false),
                    direction = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    first_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    request_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_elevator_pending_call", x => new { x.elevator_id, x.position });
                    table.CheckConstraint("ck_elevator_pending_call_kind", "(kind = 'hall' AND direction IN ('up', 'down')) OR (kind = 'car' AND direction IS NULL)");
                    table.CheckConstraint("ck_elevator_pending_call_request_count", "request_count > 0");
                    table.ForeignKey(
                        name: "fk_elevator_pending_call_elevator_state",
                        column: x => x.elevator_id,
                        principalSchema: "elevator",
                        principalTable: "elevator_state",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_processed_command_processed_at",
                schema: "elevator",
                table: "processed_command",
                column: "processed_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "elevator_pending_call",
                schema: "elevator");

            migrationBuilder.DropTable(
                name: "processed_command",
                schema: "elevator");

            migrationBuilder.DropTable(
                name: "elevator_state",
                schema: "elevator");
        }
    }
}
