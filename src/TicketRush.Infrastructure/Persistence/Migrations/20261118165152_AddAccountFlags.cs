using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketRush.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_flags",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    detail = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_flags", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_account_flags_created_at",
                table: "account_flags",
                column: "created_at",
                filter: "resolved_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_account_flags_user_id_reason",
                table: "account_flags",
                columns: new[] { "user_id", "reason" },
                unique: true,
                filter: "resolved_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_flags");
        }
    }
}
