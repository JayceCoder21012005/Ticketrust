using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketRush.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    changes = table.Column<string>(type: "jsonb", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_entries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_action_at",
                table: "audit_entries",
                columns: new[] { "action", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_actor_user_id_at",
                table: "audit_entries",
                columns: new[] { "actor_user_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_at_id",
                table: "audit_entries",
                columns: new[] { "at", "id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_entity_type_entity_id_at",
                table: "audit_entries",
                columns: new[] { "entity_type", "entity_id", "at" });
            // Spec 16: the audit log is append-only at the database, whatever role connects (the owner included).
            migrationBuilder.Sql("""
                CREATE FUNCTION audit_entries_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'audit_entries is append-only: % is not allowed', TG_OP USING ERRCODE = 'insufficient_privilege';
                END;
                $$;
                CREATE TRIGGER audit_entries_no_update_delete BEFORE UPDATE OR DELETE ON audit_entries
                    FOR EACH ROW EXECUTE FUNCTION audit_entries_append_only();
                CREATE TRIGGER audit_entries_no_truncate BEFORE TRUNCATE ON audit_entries
                    FOR EACH STATEMENT EXECUTE FUNCTION audit_entries_append_only();
                REVOKE UPDATE, DELETE, TRUNCATE ON audit_entries FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit_entries_no_update_delete ON audit_entries; DROP TRIGGER IF EXISTS audit_entries_no_truncate ON audit_entries; DROP FUNCTION IF EXISTS audit_entries_append_only();");

            migrationBuilder.DropTable(
                name: "audit_entries");
        }
    }
}
