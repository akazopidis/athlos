using System;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Athlos.Catalog.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SearchQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The server allows pg_trgm through azure.extensions (infra/modules/postgres.bicep); it is a
            // trusted extension, so the database's owner may create it.
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search_vector",
                table: "programs",
                type: "tsvector",
                nullable: true,
                computedColumnSql: "setweight(to_tsvector('english', title), 'A') || setweight(to_tsvector('english', description), 'B')",
                stored: true);

            migrationBuilder.CreateTable(
                name: "coach_names",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_coach_names", x => x.tenant_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_programs_search_vector",
                table: "programs",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_programs_title",
                table: "programs",
                column: "title")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_coach_names_display_name",
                table: "coach_names",
                column: "display_name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            // Public like the programs it names (design §8): readable in the public-read scope, written
            // only inside its own tenant (a split policy, so reads widen and writes do not).
            migrationBuilder.AddTenantIsolationPolicy("coach_names", alsoReadable: RowLevelSecurity.PublicRead);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "coach_names");

            migrationBuilder.DropIndex(
                name: "ix_programs_search_vector",
                table: "programs");

            migrationBuilder.DropIndex(
                name: "ix_programs_title",
                table: "programs");

            migrationBuilder.DropColumn(
                name: "search_vector",
                table: "programs");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");
        }
    }
}
