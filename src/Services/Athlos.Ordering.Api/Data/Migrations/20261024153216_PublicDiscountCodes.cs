using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Ordering.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PublicDiscountCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_public",
                table: "discount_codes",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_public",
                table: "discount_codes");
        }
    }
}
