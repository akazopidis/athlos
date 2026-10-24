using System;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Ordering.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccountCart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "carts",
                columns: table => new
                {
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lines = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_carts", x => x.customer_id);
                });

            // Layer 4 (design §3.2): a cart is the customer's, like their checkouts. Only a session acting
            // for that customer reads or writes it; without a user, nothing is visible.
            migrationBuilder.EnableRowLevelSecurity("carts");
            migrationBuilder.Sql($"CREATE POLICY own_carts ON carts USING (customer_id = {RowLevelSecurity.CurrentUser}) WITH CHECK (customer_id = {RowLevelSecurity.CurrentUser});");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "carts");
        }
    }
}
