using System;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Enrollment.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "enrollments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    program_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    program_title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    coach_slug = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    enrolled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_enrollments", x => x.id);
                    table.UniqueConstraint("ak_enrollments_id_tenant_id_customer_id", x => new { x.id, x.tenant_id, x.customer_id });
                });

            migrationBuilder.CreateTable(
                name: "workout_progress",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enrollment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workout_id = table.Column<Guid>(type: "uuid", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workout_progress", x => x.id);
                    table.ForeignKey(
                        name: "fk_workout_progress_enrollments_enrollment_id_tenant_id_custom",
                        columns: x => new { x.enrollment_id, x.tenant_id, x.customer_id },
                        principalTable: "enrollments",
                        principalColumns: new[] { "id", "tenant_id", "customer_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_enrollments_customer_id_program_id",
                table: "enrollments",
                columns: new[] { "customer_id", "program_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_enrollments_order_id",
                table: "enrollments",
                column: "order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_enrollments_tenant_id_program_id",
                table: "enrollments",
                columns: new[] { "tenant_id", "program_id" });

            migrationBuilder.CreateIndex(
                name: "ix_workout_progress_customer_id",
                table: "workout_progress",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_workout_progress_enrollment_id_tenant_id_customer_id",
                table: "workout_progress",
                columns: new[] { "enrollment_id", "tenant_id", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "ix_workout_progress_enrollment_id_workout_id",
                table: "workout_progress",
                columns: new[] { "enrollment_id", "workout_id" },
                unique: true);

            // Layer 4 (design §3.2). Written by hand; EF does not generate policies.
            // Like orders: the coach's tenant sees its rows, the customer sees their own across tenants
            // (design §1). Writes stay inside the current tenant (split policies), so a customer writes
            // progress only after entering the enrollment's tenant, and never changes an enrollment.
            migrationBuilder.AddTenantIsolationPolicy("enrollments", alsoReadable: $"customer_id = {RowLevelSecurity.CurrentUser}");
            migrationBuilder.AddTenantIsolationPolicy("workout_progress", alsoReadable: $"customer_id = {RowLevelSecurity.CurrentUser}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workout_progress");

            migrationBuilder.DropTable(
                name: "enrollments");
        }
    }
}
