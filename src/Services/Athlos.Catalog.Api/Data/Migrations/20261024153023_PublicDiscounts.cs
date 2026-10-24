using System;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Catalog.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PublicDiscounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "public_discounts",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    percent_off = table.Column<int>(type: "integer", nullable: false),
                    program_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_public_discounts", x => x.tenant_id);
                });

            // Public like the programs it is shown on (design §2.4): readable in the public-read scope,
            // written only inside its own tenant (a split policy, so reads widen and writes do not).
            migrationBuilder.AddTenantIsolationPolicy("public_discounts", alsoReadable: RowLevelSecurity.PublicRead);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "public_discounts");
        }
    }
}
