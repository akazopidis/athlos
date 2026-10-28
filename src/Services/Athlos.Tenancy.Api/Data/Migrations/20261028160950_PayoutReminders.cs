using System;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Tenancy.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PayoutReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payout_reminders",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payout_reminders", x => x.tenant_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payout_reminders_due_at",
                table: "payout_reminders",
                column: "due_at");

            // A work queue of the service, like the outbox: a tenant id and a time, no tenant data. RLS is on
            // with an explicit allow-all policy, so the choice is visible.
            migrationBuilder.EnableRowLevelSecurity("payout_reminders");
            migrationBuilder.Sql("CREATE POLICY service_only ON payout_reminders USING (true) WITH CHECK (true);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payout_reminders");
        }
    }
}
