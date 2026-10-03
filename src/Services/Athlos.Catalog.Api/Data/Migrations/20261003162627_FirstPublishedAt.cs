using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Catalog.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class FirstPublishedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "first_published_at",
                table: "programs",
                type: "timestamp with time zone",
                nullable: true);

            // Backfill programs on sale now. FORCE ROW LEVEL SECURITY applies to the owner too, so the
            // migrator would see no rows without a tenant; lift it for this one update, inside the
            // migration's transaction, and put it back. Programs unpublished before this migration
            // cannot be told apart from drafts and stay deletable.
            migrationBuilder.Sql("ALTER TABLE programs NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("UPDATE programs SET first_published_at = published_at WHERE published_at IS NOT NULL;");
            migrationBuilder.Sql("ALTER TABLE programs FORCE ROW LEVEL SECURITY;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "first_published_at",
                table: "programs");
        }
    }
}
