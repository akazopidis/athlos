using System;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Enrollment.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class EmailPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_preferences",
                columns: table => new
                {
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    program_updates = table.Column<bool>(type: "boolean", nullable: false),
                    review_replies = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_preferences", x => x.customer_id);
                });

            // The customer's own row, not a tenant's (design §5.3), like catalog's saved_programs: one
            // policy for every command. The senders run inside a coach's tenant and read, never write,
            // the rows of that coach's customers: the subquery on enrollments is itself under RLS, so it
            // sees the current tenant's enrollments only (and the caller's own).
            migrationBuilder.EnableRowLevelSecurity("email_preferences");
            migrationBuilder.Sql($"CREATE POLICY own_email_preferences ON email_preferences USING (customer_id = {RowLevelSecurity.CurrentUser}) WITH CHECK (customer_id = {RowLevelSecurity.CurrentUser});");
            migrationBuilder.Sql("CREATE POLICY tenant_customers_read ON email_preferences FOR SELECT USING (EXISTS (SELECT 1 FROM enrollments e WHERE e.customer_id = email_preferences.customer_id));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_preferences");
        }
    }
}
