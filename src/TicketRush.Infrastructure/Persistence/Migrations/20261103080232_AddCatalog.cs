using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace TicketRush.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,");

            migrationBuilder.AddColumn<string>(
                name: "city",
                table: "events",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            // Events created before this migration copy their venue's city (the domain does it for new ones).
            migrationBuilder.Sql("UPDATE events SET city = venues.city FROM venues WHERE venues.id = events.venue_id;");

            // unaccent() is only STABLE (it reads a dictionary), so generated columns and indexes need an IMMUTABLE
            // wrapper that pins the dictionary. Both are safe because the dictionary never changes at runtime.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION immutable_unaccent(value text) RETURNS text
                    LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT
                    RETURN public.unaccent('public.unaccent'::regdictionary, value);

                CREATE OR REPLACE FUNCTION catalog_search_text(title text, lineup text[], city text) RETURNS text
                    LANGUAGE sql IMMUTABLE PARALLEL SAFE
                    RETURN lower(immutable_unaccent(
                        coalesce(title, '') || ' ' || coalesce(array_to_string(lineup, ' '), '') || ' ' || coalesce(city, '')));

                -- Full-text match first ("son tung" finds "Sơn Tùng"); trigram word similarity catches typos ("son tunng").
                -- A single-expression SQL function is inlined by the planner, so both GIN indexes stay usable.
                CREATE OR REPLACE FUNCTION catalog_matches(search_vector tsvector, search_text text, query text) RETURNS boolean
                    LANGUAGE sql IMMUTABLE PARALLEL SAFE
                    RETURN search_vector @@ websearch_to_tsquery('simple'::regconfig, lower(immutable_unaccent(query)))
                        OR lower(immutable_unaccent(query)) <% search_text;
                """);

            migrationBuilder.AddColumn<string>(
                name: "search_text",
                table: "events",
                type: "text",
                nullable: true,
                computedColumnSql: "catalog_search_text(title, lineup, city)",
                stored: true);

            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search_vector",
                table: "events",
                type: "tsvector",
                nullable: true,
                computedColumnSql: "to_tsvector('simple'::regconfig, catalog_search_text(title, lineup, city))",
                stored: true);

            migrationBuilder.CreateTable(
                name: "event_seats",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    index = table.Column<int>(type: "integer", nullable: false),
                    section = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    row = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    number = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    x = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false),
                    y = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false),
                    tier_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_seats", x => x.id);
                    table.ForeignKey(
                        name: "fk_event_seats_events_event_id",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_event_seats_ticket_tier_tier_id",
                        column: x => x.tier_id,
                        principalTable: "ticket_tiers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_events_category_starts_at",
                table: "events",
                columns: new[] { "category", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "ix_events_city_starts_at",
                table: "events",
                columns: new[] { "city", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "ix_events_search_text",
                table: "events",
                column: "search_text")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_events_search_vector",
                table: "events",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_event_seats_event_id_index",
                table: "event_seats",
                columns: new[] { "event_id", "index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_event_seats_event_id_section_row_number",
                table: "event_seats",
                columns: new[] { "event_id", "section", "row", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_event_seats_tier_id",
                table: "event_seats",
                column: "tier_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "event_seats");

            migrationBuilder.DropIndex(
                name: "ix_events_category_starts_at",
                table: "events");

            migrationBuilder.DropIndex(
                name: "ix_events_city_starts_at",
                table: "events");

            migrationBuilder.DropIndex(
                name: "ix_events_search_text",
                table: "events");

            migrationBuilder.DropIndex(
                name: "ix_events_search_vector",
                table: "events");

            migrationBuilder.DropColumn(
                name: "search_text",
                table: "events");

            migrationBuilder.DropColumn(
                name: "search_vector",
                table: "events");

            migrationBuilder.DropColumn(
                name: "city",
                table: "events");

            // Functions depend on the unaccent extension, so they go first.
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS catalog_matches(tsvector, text, text); DROP FUNCTION IF EXISTS catalog_search_text(text, text[], text); DROP FUNCTION IF EXISTS immutable_unaccent(text);");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,");
        }
    }
}
