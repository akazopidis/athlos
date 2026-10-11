using System;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Ordering.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Cart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_orders_stripe_checkout_session_id",
                table: "orders");

            migrationBuilder.AddColumn<Guid>(
                name: "checkout_id",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "seller_account_id",
                table: "orders",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stripe_transfer_id",
                table: "orders",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "checkouts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    stripe_checkout_session_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    checkout_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_checkouts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_orders_checkout_id",
                table: "orders",
                column: "checkout_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_stripe_checkout_session_id",
                table: "orders",
                column: "stripe_checkout_session_id");

            migrationBuilder.CreateIndex(
                name: "ix_checkouts_customer_id_idempotency_key",
                table: "checkouts",
                columns: new[] { "customer_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_checkouts_stripe_checkout_session_id",
                table: "checkouts",
                column: "stripe_checkout_session_id",
                unique: true);

            // Layer 4 (design §3.2): a cart checkout is the customer's, across coaches. Only a session
            // acting for that customer reads or writes it; without a user, nothing is visible.
            migrationBuilder.EnableRowLevelSecurity("checkouts");
            migrationBuilder.Sql($"CREATE POLICY own_checkouts ON checkouts USING (customer_id = {RowLevelSecurity.CurrentUser}) WITH CHECK (customer_id = {RowLevelSecurity.CurrentUser});");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "checkouts");

            migrationBuilder.DropIndex(
                name: "ix_orders_checkout_id",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "ix_orders_stripe_checkout_session_id",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "checkout_id",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "seller_account_id",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "stripe_transfer_id",
                table: "orders");

            migrationBuilder.CreateIndex(
                name: "ix_orders_stripe_checkout_session_id",
                table: "orders",
                column: "stripe_checkout_session_id",
                unique: true);
        }
    }
}
