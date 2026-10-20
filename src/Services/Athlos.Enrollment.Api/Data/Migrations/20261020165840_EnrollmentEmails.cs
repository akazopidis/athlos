using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Enrollment.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnrollmentEmails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "customer_email",
                table: "enrollments",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "update_emailed_at",
                table: "enrollments",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "customer_email",
                table: "enrollments");

            migrationBuilder.DropColumn(
                name: "update_emailed_at",
                table: "enrollments");
        }
    }
}
