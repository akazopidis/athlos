using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Tenancy.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SplitMembershipPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The Initial policy on memberships combined "own tenant OR own user" in one policy, and a
            // policy's USING clause also governs DELETE. A user could therefore delete their own
            // membership rows in other tenants. Reads stay wide; writes become tenant-only.
            migrationBuilder.AddTenantIsolationPolicy("memberships", alsoReadable: $"user_id = {RowLevelSecurity.CurrentUser}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
