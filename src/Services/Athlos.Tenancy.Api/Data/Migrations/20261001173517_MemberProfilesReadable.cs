using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Tenancy.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MemberProfilesReadable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Inside a tenant, the profiles of that tenant's members become readable (SELECT only), so
            // the sale email can reach the workspace owners (design §5.3). own_profile still decides every
            // write. The subquery on memberships runs under memberships' own RLS: only the current
            // tenant's memberships, so a session sees no profile of another tenant's members.
            migrationBuilder.Sql($"""
                CREATE POLICY tenant_members_read ON user_profiles FOR SELECT
                USING (user_id IN (SELECT m.user_id FROM memberships m WHERE m.tenant_id = {RowLevelSecurity.CurrentTenant}));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_members_read ON user_profiles;");
        }
    }
}
