using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Enrollment.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PriceDropEmails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "price_drop_emailed_at",
                table: "email_preferences",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "price_drops",
                table: "email_preferences",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "price_drop_emailed_at",
                table: "email_preferences");

            migrationBuilder.DropColumn(
                name: "price_drops",
                table: "email_preferences");
        }
    }
}
