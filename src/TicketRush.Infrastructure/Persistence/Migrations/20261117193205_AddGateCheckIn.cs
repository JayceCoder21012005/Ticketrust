using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketRush.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGateCheckIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "checked_in_at",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "checked_in_by_user_id",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "check_ins",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    staff_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gate_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    scanned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    result = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_check_ins", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "gate_assignments",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gate_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gate_assignments", x => new { x.event_id, x.user_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_event_status",
                table: "tickets",
                columns: new[] { "event_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_check_ins_event_scanned_at",
                table: "check_ins",
                columns: new[] { "event_id", "scanned_at" });

            migrationBuilder.CreateIndex(
                name: "ix_check_ins_ticket",
                table: "check_ins",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_gate_assignments_user_id",
                table: "gate_assignments",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "check_ins");

            migrationBuilder.DropTable(
                name: "gate_assignments");

            migrationBuilder.DropIndex(
                name: "ix_tickets_event_status",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "checked_in_at",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "checked_in_by_user_id",
                table: "tickets");
        }
    }
}
