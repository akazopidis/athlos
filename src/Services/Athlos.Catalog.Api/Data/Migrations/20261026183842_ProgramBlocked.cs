using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Catalog.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProgramBlocked : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "blocked_at",
                table: "programs",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "blocked_at",
                table: "programs");
        }
    }
}
