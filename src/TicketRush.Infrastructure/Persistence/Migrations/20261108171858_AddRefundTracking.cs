using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketRush.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "refund_amount",
                table: "payment_attempts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "refund_attempts",
                table: "payment_attempts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "refund_currency",
                table: "payment_attempts",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "refund_last_error",
                table: "payment_attempts",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "refund_needs_attention",
                table: "payment_attempts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "refund_next_attempt_at",
                table: "payment_attempts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "refund_attempts",
                table: "orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "refund_last_error",
                table: "orders",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "refund_needs_attention",
                table: "orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "refund_next_attempt_at",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            // Session refunds requested before this migration only recorded their amount on the outbox event: copy it over,
            // so the refund worker sends back exactly what was promised (rows it cannot fill are parked by the worker).
            migrationBuilder.Sql("""
                UPDATE payment_attempts pa
                SET refund_amount = (m.payload ->> 'amount')::bigint, refund_currency = m.payload ->> 'currency'
                FROM outbox_messages m
                WHERE pa.status = 'RefundPending' AND pa.refund_amount IS NULL
                  AND m.type = 'PaymentRefundRequested' AND (m.payload ->> 'attemptId')::uuid = pa.id
                """);

            migrationBuilder.CreateIndex(
                name: "ix_payment_attempts_refund_pending",
                table: "payment_attempts",
                column: "id",
                filter: "status = 'RefundPending'");

            migrationBuilder.CreateIndex(
                name: "ix_orders_refund_pending",
                table: "orders",
                column: "id",
                filter: "status = 'RefundPending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_payment_attempts_refund_pending",
                table: "payment_attempts");

            migrationBuilder.DropIndex(
                name: "ix_orders_refund_pending",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "refund_amount",
                table: "payment_attempts");

            migrationBuilder.DropColumn(
                name: "refund_attempts",
                table: "payment_attempts");

            migrationBuilder.DropColumn(
                name: "refund_currency",
                table: "payment_attempts");

            migrationBuilder.DropColumn(
                name: "refund_last_error",
                table: "payment_attempts");

            migrationBuilder.DropColumn(
                name: "refund_needs_attention",
                table: "payment_attempts");

            migrationBuilder.DropColumn(
                name: "refund_next_attempt_at",
                table: "payment_attempts");

            migrationBuilder.DropColumn(
                name: "refund_attempts",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "refund_last_error",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "refund_needs_attention",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "refund_next_attempt_at",
                table: "orders");
        }
    }
}
