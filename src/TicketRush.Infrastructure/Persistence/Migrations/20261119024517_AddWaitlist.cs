using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketRush.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWaitlist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "waitlist_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tier_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    queued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    misses = table.Column<int>(type: "integer", nullable: false),
                    offer_hold_id = table.Column<Guid>(type: "uuid", nullable: true),
                    offer_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_waitlist_entries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_waitlist_entries_event_id_queued_at",
                table: "waitlist_entries",
                columns: new[] { "event_id", "queued_at" },
                filter: "status = 'Waiting'");

            migrationBuilder.CreateIndex(
                name: "ix_waitlist_entries_event_id_user_id",
                table: "waitlist_entries",
                columns: new[] { "event_id", "user_id" },
                unique: true,
                filter: "status IN ('Waiting', 'Offered')");

            migrationBuilder.CreateIndex(
                name: "ix_waitlist_entries_offer_expires_at",
                table: "waitlist_entries",
                column: "offer_expires_at",
                filter: "status = 'Offered'");

            migrationBuilder.CreateIndex(
                name: "ix_waitlist_entries_user_id",
                table: "waitlist_entries",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "waitlist_entries");
        }
    }
}
