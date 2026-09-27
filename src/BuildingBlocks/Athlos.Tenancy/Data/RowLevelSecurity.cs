using Microsoft.EntityFrameworkCore.Migrations;

namespace Athlos.Tenancy.Data;

// Helpers for RLS policies in migrations (design §3.2, layer 4). The settings are written per
// transaction by TenantSessionInterceptor; an unset setting is '' and becomes NULL here, which
// matches no row, so a missing tenant fails closed.
public static class RowLevelSecurity
{
    public const string CurrentTenant = "NULLIF(current_setting('app.tenant_id', true), '')::uuid";
    public const string CurrentUser = "NULLIF(current_setting('app.user_id', true), '')::uuid";
    public const string PublicRead = "current_setting('app.scope', true) = 'public-read'";

    // ENABLE applies policies to normal roles; FORCE applies them to the table owner too.
    // Superusers and BYPASSRLS roles still skip them, so app roles must never have either.
    public static void EnableRowLevelSecurity(this MigrationBuilder migrationBuilder, string table)
    {
        migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
        migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
    }

    // The standard policy for an ITenantOwned table: read and write only the current tenant's rows.
    // alsoReadable widens reads only (for example the caller's own rows, or published rows);
    // writes always stay inside the current tenant.
    public static void AddTenantIsolationPolicy(this MigrationBuilder migrationBuilder, string table, string? alsoReadable = null)
    {
        var readable = alsoReadable is null ? $"tenant_id = {CurrentTenant}" : $"tenant_id = {CurrentTenant} OR ({alsoReadable})";

        migrationBuilder.EnableRowLevelSecurity(table);
        migrationBuilder.Sql(
            $"CREATE POLICY tenant_isolation ON {table} USING ({readable}) WITH CHECK (tenant_id = {CurrentTenant});");
    }
}
