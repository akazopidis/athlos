using System;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Enrollment.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Reviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reviews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enrollment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    program_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rating = table.Column<int>(type: "integer", nullable: false),
                    comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    author_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    reply = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    replied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reviews", x => x.id);
                    table.CheckConstraint("ck_reviews_rating", "rating BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "fk_reviews_enrollments_enrollment_id_tenant_id_customer_id",
                        columns: x => new { x.enrollment_id, x.tenant_id, x.customer_id },
                        principalTable: "enrollments",
                        principalColumns: new[] { "id", "tenant_id", "customer_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_reviews_customer_id",
                table: "reviews",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_reviews_enrollment_id",
                table: "reviews",
                column: "enrollment_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reviews_enrollment_id_tenant_id_customer_id",
                table: "reviews",
                columns: new[] { "enrollment_id", "tenant_id", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "ix_reviews_program_id",
                table: "reviews",
                column: "program_id");

            migrationBuilder.CreateIndex(
                name: "ix_reviews_tenant_id_program_id_created_at",
                table: "reviews",
                columns: new[] { "tenant_id", "program_id", "created_at" });

            // Design §2.4: the tenant's rows; the customer also reads their own, and the public-read
            // scope (a program's page) reads every review. Writes stay inside the tenant (split policies).
            migrationBuilder.AddTenantIsolationPolicy("reviews",
                alsoReadable: $"customer_id = {RowLevelSecurity.CurrentUser} OR {RowLevelSecurity.PublicRead}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reviews");
        }
    }
}
