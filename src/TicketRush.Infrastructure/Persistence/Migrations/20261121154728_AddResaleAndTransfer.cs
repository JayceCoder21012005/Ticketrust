using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketRush.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddResaleAndTransfer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "offer_listing_id",
                table: "waitlist_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "orders",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Primary");

            migrationBuilder.AddColumn<Guid>(
                name: "listing_id",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "resale_listings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seller_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seat_index = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    waitlist_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    offered_to_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reserved_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    price_amount = table.Column<long>(type: "bigint", nullable: false),
                    price_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_resale_listings", x => x.id);
                    table.ForeignKey(
                        name: "fk_resale_listings_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ticket_transfers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    to_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_transfers", x => x.id);
                    table.ForeignKey(
                        name: "fk_ticket_transfers_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "seller_payouts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    listing_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seller_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    amount_amount = table.Column<long>(type: "bigint", nullable: false),
                    amount_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_seller_payouts", x => x.id);
                    table.ForeignKey(
                        name: "fk_seller_payouts_resale_listings_listing_id",
                        column: x => x.listing_id,
                        principalTable: "resale_listings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_resale_listings_event_id_tier_id",
                table: "resale_listings",
                columns: new[] { "event_id", "tier_id" },
                filter: "status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ix_resale_listings_seller_id",
                table: "resale_listings",
                column: "seller_id");

            migrationBuilder.CreateIndex(
                name: "ux_resale_listings_open_ticket",
                table: "resale_listings",
                column: "ticket_id",
                unique: true,
                filter: "status IN ('Active', 'Reserved')");

            migrationBuilder.CreateIndex(
                name: "ix_seller_payouts_event_id",
                table: "seller_payouts",
                column: "event_id",
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_seller_payouts_listing_id",
                table: "seller_payouts",
                column: "listing_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_seller_payouts_seller_id",
                table: "seller_payouts",
                column: "seller_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_transfers_event_id",
                table: "ticket_transfers",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_transfers_expires_at",
                table: "ticket_transfers",
                column: "expires_at",
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ux_ticket_transfers_pending_ticket",
                table: "ticket_transfers",
                column: "ticket_id",
                unique: true,
                filter: "status = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "seller_payouts");

            migrationBuilder.DropTable(
                name: "ticket_transfers");

            migrationBuilder.DropTable(
                name: "resale_listings");

            migrationBuilder.DropColumn(
                name: "offer_listing_id",
                table: "waitlist_entries");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "listing_id",
                table: "orders");
        }
    }
}
