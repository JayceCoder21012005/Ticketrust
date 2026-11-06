using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketRush.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProcessedWebhooksPerProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_processed_webhooks",
                table: "processed_webhooks");

            migrationBuilder.AddPrimaryKey(
                name: "pk_processed_webhooks",
                table: "processed_webhooks",
                columns: new[] { "provider", "provider_event_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_processed_webhooks",
                table: "processed_webhooks");

            migrationBuilder.AddPrimaryKey(
                name: "pk_processed_webhooks",
                table: "processed_webhooks",
                column: "provider_event_id");
        }
    }
}
