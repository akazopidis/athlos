using System;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athlos.Tenancy.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    plan = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    stripe_account_id = table.Column<string>(type: "text", nullable: true),
                    stripe_onboarding_complete = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    branding = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "memberships",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_memberships", x => x.id);
                    table.ForeignKey(
                        name: "fk_memberships_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_memberships_tenant_id_user_id",
                table: "memberships",
                columns: new[] { "tenant_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_memberships_user_id",
                table: "memberships",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_tenants_slug",
                table: "tenants",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_profiles_user_id",
                table: "user_profiles",
                column: "user_id",
                unique: true);

            // Layer 4 (design §3.2). Written by hand; EF does not generate policies.

            // tenants: every session may read (storefronts, slug lookup); a session may create,
            // change or delete only the tenant it is acting for.
            migrationBuilder.EnableRowLevelSecurity("tenants");
            migrationBuilder.Sql("CREATE POLICY tenants_read ON tenants FOR SELECT USING (true);");
            migrationBuilder.Sql($"CREATE POLICY tenants_insert ON tenants FOR INSERT WITH CHECK (id = {RowLevelSecurity.CurrentTenant});");
            migrationBuilder.Sql($"CREATE POLICY tenants_update ON tenants FOR UPDATE USING (id = {RowLevelSecurity.CurrentTenant}) WITH CHECK (id = {RowLevelSecurity.CurrentTenant});");
            migrationBuilder.Sql($"CREATE POLICY tenants_delete ON tenants FOR DELETE USING (id = {RowLevelSecurity.CurrentTenant});");

            // memberships: the tenant's rows, plus the caller's own memberships in other tenants
            // (the B2B2C rule from design §1, used by GET /api/v1/me/memberships).
            migrationBuilder.AddTenantIsolationPolicy("memberships", alsoReadable: $"user_id = {RowLevelSecurity.CurrentUser}");

            // user_profiles: owned by the user, not a tenant.
            migrationBuilder.EnableRowLevelSecurity("user_profiles");
            migrationBuilder.Sql($"CREATE POLICY own_profile ON user_profiles USING (user_id = {RowLevelSecurity.CurrentUser}) WITH CHECK (user_id = {RowLevelSecurity.CurrentUser});");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "memberships");

            migrationBuilder.DropTable(
                name: "user_profiles");

            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
