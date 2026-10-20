using System;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Enrollment.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReviewModeration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "hidden_at",
                table: "reviews",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "review_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    program_id = table.Column<Guid>(type: "uuid", nullable: false),
                    program_title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    coach_slug = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    author_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    rating = table.Column<int>(type: "integer", nullable: false),
                    comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    reporter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    hidden = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_reports", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_review_reports_resolved_at",
                table: "review_reports",
                column: "resolved_at");

            migrationBuilder.CreateIndex(
                name: "ix_review_reports_review_id_reporter_id",
                table: "review_reports",
                columns: new[] { "review_id", "reporter_id" },
                unique: true);

            // A work queue of the service, like ordering's payout_retries (design §2.4): the platform admin
            // lists it without a tenant, and it holds copies, never a way into a tenant's rows. RLS is on
            // with an explicit allow-all policy, so the choice is visible, not an omission.
            migrationBuilder.EnableRowLevelSecurity("review_reports");
            migrationBuilder.Sql("CREATE POLICY service_only ON review_reports USING (true) WITH CHECK (true);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "review_reports");

            migrationBuilder.DropColumn(
                name: "hidden_at",
                table: "reviews");
        }
    }
}
