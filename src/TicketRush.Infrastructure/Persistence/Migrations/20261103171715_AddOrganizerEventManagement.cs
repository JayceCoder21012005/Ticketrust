using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketRush.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizerEventManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "organizer_id",
                table: "venues",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "layout",
                table: "events",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_venues_organizer_id",
                table: "venues",
                column: "organizer_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_venues_organizer_id",
                table: "venues");

            migrationBuilder.DropColumn(
                name: "organizer_id",
                table: "venues");

            migrationBuilder.DropColumn(
                name: "layout",
                table: "events");
        }
    }
}
