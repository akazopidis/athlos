using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Tenancy.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CoachNameCopies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "catalog_name",
                table: "tenants",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "catalog_name",
                table: "tenants");
        }
    }
}
