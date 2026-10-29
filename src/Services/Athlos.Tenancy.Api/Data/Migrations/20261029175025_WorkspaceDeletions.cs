using System;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Tenancy.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkspaceDeletions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workspace_deletions",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_error = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspace_deletions", x => x.tenant_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_workspace_deletions_next_attempt_at",
                table: "workspace_deletions",
                column: "next_attempt_at");

            // A work queue of the service, like payout_reminders: a tenant id, who asked and the retry
            // state, no tenant data. RLS is on with an explicit allow-all policy, so the choice is visible.
            migrationBuilder.EnableRowLevelSecurity("workspace_deletions");
            migrationBuilder.Sql("CREATE POLICY service_only ON workspace_deletions USING (true) WITH CHECK (true);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workspace_deletions");
        }
    }
}
