using System;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Enrollment.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AdminActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_actions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    admin_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    detail = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_admin_actions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_admin_actions_at",
                table: "admin_actions",
                column: "at");

            // The service's audit trail of admin actions, like payout_retries a table of the service and
            // not a tenant's data: RLS is on with an explicit allow-all policy, so the choice is visible.
            // Only admin routes read it.
            migrationBuilder.EnableRowLevelSecurity("admin_actions");
            migrationBuilder.Sql("CREATE POLICY service_only ON admin_actions USING (true) WITH CHECK (true);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_actions");
        }
    }
}
