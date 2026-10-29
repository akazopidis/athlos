using System;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Tenancy.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class DataExports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "data_exports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ready_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_exports", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_data_exports_expires_at",
                table: "data_exports",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_data_exports_next_attempt_at",
                table: "data_exports",
                column: "next_attempt_at");

            migrationBuilder.CreateIndex(
                name: "ix_data_exports_requested_by_requested_at",
                table: "data_exports",
                columns: new[] { "requested_by", "requested_at" });

            // A work queue of the service, like workspace_deletions: ids, times and the retry state, never
            // the exported data. RLS is on with an explicit allow-all policy, so the choice is visible.
            migrationBuilder.EnableRowLevelSecurity("data_exports");
            migrationBuilder.Sql("CREATE POLICY service_only ON data_exports USING (true) WITH CHECK (true);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_exports");
        }
    }
}
