using System;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Tenancy.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ContentReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "content_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    program_id = table.Column<Guid>(type: "uuid", nullable: true),
                    subject = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    coach_slug = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    reporter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_content_reports", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_content_reports_program_id",
                table: "content_reports",
                column: "program_id");

            migrationBuilder.CreateIndex(
                name: "ix_content_reports_resolved_at_reported_at",
                table: "content_reports",
                columns: new[] { "resolved_at", "reported_at" });

            migrationBuilder.CreateIndex(
                name: "ix_content_reports_tenant_id",
                table: "content_reports",
                column: "tenant_id");

            // A work queue of the service, like the outbox: it copies what the admin's list shows and
            // reads no tenant's rows. RLS is on with an explicit allow-all policy, so the choice is visible.
            migrationBuilder.EnableRowLevelSecurity("content_reports");
            migrationBuilder.Sql("CREATE POLICY service_only ON content_reports USING (true) WITH CHECK (true);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "content_reports");
        }
    }
}
