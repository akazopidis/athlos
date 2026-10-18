using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Catalog.Api.Data.Migrations
{
    // The program page's outline and free preview (design §8): copies on the public program row, so the
    // workouts table keeps no public-read policy.
    /// <inheritdoc />
    public partial class ProgramOutline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_sample",
                table: "workouts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "outline",
                table: "programs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sample",
                table: "programs",
                type: "jsonb",
                nullable: true);

            // Backfill the outline of every program from its workouts (none is a preview yet). FORCE ROW
            // LEVEL SECURITY applies to the owner too, so it is lifted for this one transaction, as in
            // FirstPublishedAt; the policies themselves do not change.
            migrationBuilder.Sql("ALTER TABLE programs NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE workouts NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                UPDATE programs p SET outline = COALESCE(
                    (SELECT jsonb_agg(jsonb_build_object('Week', w.week, 'Day', w.day, 'Title', w.title, 'Sample', false) ORDER BY w.week, w.day)
                     FROM workouts w WHERE w.program_id = p.id),
                    '[]'::jsonb);
                """);
            migrationBuilder.Sql("ALTER TABLE workouts FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE programs FORCE ROW LEVEL SECURITY;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_sample",
                table: "workouts");

            migrationBuilder.DropColumn(
                name: "outline",
                table: "programs");

            migrationBuilder.DropColumn(
                name: "sample",
                table: "programs");
        }
    }
}
