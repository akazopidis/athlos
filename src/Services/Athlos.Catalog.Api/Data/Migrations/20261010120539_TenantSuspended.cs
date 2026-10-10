using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Catalog.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TenantSuspended : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "tenant_suspended",
                table: "programs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // The marketplace's public read also leaves out a suspended coach's programs (design §3.4),
            // so RLS hides them even if a slice forgot the filter. Writes stay own-tenant only.
            migrationBuilder.AddTenantIsolationPolicy("programs", alsoReadable: $"{RowLevelSecurity.PublicRead} AND is_published AND NOT tenant_suspended");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddTenantIsolationPolicy("programs", alsoReadable: $"{RowLevelSecurity.PublicRead} AND is_published");

            migrationBuilder.DropColumn(
                name: "tenant_suspended",
                table: "programs");
        }
    }
}
