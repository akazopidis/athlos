using System;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Ordering.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RefundRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "refund_request_note",
                table: "orders",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "refund_requested_at",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "refund_requests",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    coach_slug = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    program_title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refund_requests", x => x.order_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_refund_requests_due_at",
                table: "refund_requests",
                column: "due_at");

            // A work queue of the service, like payout_retries: it names an order and its tenant and
            // copies what the admin's list shows. RLS is on with an explicit allow-all policy, so the
            // choice is visible; the admin's refund reads the order inside its tenant.
            migrationBuilder.EnableRowLevelSecurity("refund_requests");
            migrationBuilder.Sql("CREATE POLICY service_only ON refund_requests USING (true) WITH CHECK (true);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "refund_requests");

            migrationBuilder.DropColumn(
                name: "refund_request_note",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "refund_requested_at",
                table: "orders");
        }
    }
}
