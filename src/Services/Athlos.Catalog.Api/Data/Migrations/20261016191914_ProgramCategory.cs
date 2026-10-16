using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Catalog.Api.Data.Migrations
{
    // Categories replace levels (design §8). A level says nothing about the category, so existing
    // programs start as GeneralFitness and their coaches pick one; the demo seed sets its own. Adding the
    // column with a default fills every row as DDL, so FORCE ROW LEVEL SECURITY does not hide any.
    /// <inheritdoc />
    public partial class ProgramCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "level",
                table: "programs");

            migrationBuilder.AddColumn<string>(
                name: "category",
                table: "programs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "GeneralFitness");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "category",
                table: "programs");

            migrationBuilder.AddColumn<string>(
                name: "level",
                table: "programs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Beginner");
        }
    }
}
