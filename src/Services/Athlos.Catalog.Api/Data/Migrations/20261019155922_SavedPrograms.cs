using System;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Catalog.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SavedPrograms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "saved_programs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    program_id = table.Column<Guid>(type: "uuid", nullable: false),
                    saved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_saved_programs", x => x.id);
                    table.ForeignKey(
                        name: "fk_saved_programs_programs_program_id",
                        column: x => x.program_id,
                        principalTable: "programs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_saved_programs_customer_id_program_id",
                table: "saved_programs",
                columns: new[] { "customer_id", "program_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_saved_programs_program_id",
                table: "saved_programs",
                column: "program_id");

            // The customer's own rows, not a tenant's (design §2.4), like ordering's checkouts: one
            // policy for every command. Deleting a program cascades here as the referential action.
            migrationBuilder.EnableRowLevelSecurity("saved_programs");
            migrationBuilder.Sql($"CREATE POLICY own_saved_programs ON saved_programs USING (customer_id = {RowLevelSecurity.CurrentUser}) WITH CHECK (customer_id = {RowLevelSecurity.CurrentUser});");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "saved_programs");
        }
    }
}
