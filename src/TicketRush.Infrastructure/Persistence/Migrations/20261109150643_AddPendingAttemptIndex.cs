using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketRush.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingAttemptIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_payment_attempts_pending_expires_at",
                table: "payment_attempts",
                column: "expires_at",
                filter: "status = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_payment_attempts_pending_expires_at",
                table: "payment_attempts");
        }
    }
}
