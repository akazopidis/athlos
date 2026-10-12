using System;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Ordering.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Refunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "refunded_at",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stripe_payment_intent_id",
                table: "orders",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "payout_retries",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    coach_slug = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_error = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payout_retries", x => x.order_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_orders_stripe_payment_intent_id",
                table: "orders",
                column: "stripe_payment_intent_id");

            migrationBuilder.CreateIndex(
                name: "ix_payout_retries_next_attempt_at",
                table: "payout_retries",
                column: "next_attempt_at");

            // A work queue of the service, like the outbox: it names an order and its tenant and holds
            // no tenant data beyond the amount owed. RLS is on with an explicit allow-all policy, so the
            // choice is visible; the retry reads the order inside its tenant.
            migrationBuilder.EnableRowLevelSecurity("payout_retries");
            migrationBuilder.Sql("CREATE POLICY service_only ON payout_retries USING (true) WITH CHECK (true);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payout_retries");

            migrationBuilder.DropIndex(
                name: "ix_orders_stripe_payment_intent_id",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "refunded_at",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "stripe_payment_intent_id",
                table: "orders");
        }
    }
}
