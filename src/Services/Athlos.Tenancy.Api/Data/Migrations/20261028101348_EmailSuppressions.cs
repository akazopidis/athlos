using System;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Tenancy.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class EmailSuppressions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_suppressions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    address_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_suppressions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_email_suppressions_address_hash",
                table: "email_suppressions",
                column: "address_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_email_suppressions_recorded_at",
                table: "email_suppressions",
                column: "recorded_at");

            // The service's own list, holding hashes and no tenant's data. RLS is on with an explicit
            // allow-all policy, so the choice is visible.
            migrationBuilder.EnableRowLevelSecurity("email_suppressions");
            migrationBuilder.Sql("CREATE POLICY service_only ON email_suppressions USING (true) WITH CHECK (true);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_suppressions");
        }
    }
}
