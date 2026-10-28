using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Catalog.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SaversReadableByTheirCoach : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A coach's session reads who saved its own programs, to tell them about a lower price (design
            // §2.4), and nothing else: SELECT only, and the program must be the current tenant's (named
            // explicitly, so the public-read scope, which shows every published program, adds nobody).
            migrationBuilder.Sql($"CREATE POLICY tenant_savers_read ON saved_programs FOR SELECT USING (EXISTS (SELECT 1 FROM programs p WHERE p.id = saved_programs.program_id AND p.tenant_id = {RowLevelSecurity.CurrentTenant}));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_savers_read ON saved_programs;");
        }
    }
}
