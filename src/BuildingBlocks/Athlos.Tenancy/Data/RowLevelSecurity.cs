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

    private static readonly string[] PolicyNames = ["tenant_isolation", "tenant_read", "tenant_insert", "tenant_update", "tenant_delete"];

    // ENABLE applies policies to normal roles; FORCE applies them to the table owner too.
    // Superusers and BYPASSRLS roles still skip them, so app roles must never have either.
    public static void EnableRowLevelSecurity(this MigrationBuilder migrationBuilder, string table)
    {
        migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
        migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
    }

    // The standard policies for an ITenantOwned table: read and write only the current tenant's rows.
    //
    // alsoReadable widens reads only (the caller's own rows, or published rows). It must then be a
    // separate SELECT policy: a policy's USING clause also decides which rows UPDATE and DELETE can
    // reach, and DELETE has no WITH CHECK. One combined policy would let a public-read session
    // delete another tenant's published rows. Replaces any earlier policies, so it can be re-run.
    public static void AddTenantIsolationPolicy(this MigrationBuilder migrationBuilder, string table, string? alsoReadable = null)
    {
        var ownTenant = $"tenant_id = {CurrentTenant}";

        migrationBuilder.EnableRowLevelSecurity(table);
        foreach (var name in PolicyNames)
        {
            migrationBuilder.Sql($"DROP POLICY IF EXISTS {name} ON {table};");
        }

        if (alsoReadable is null)
        {
            migrationBuilder.Sql($"CREATE POLICY tenant_isolation ON {table} USING ({ownTenant}) WITH CHECK ({ownTenant});");
            return;
        }

        migrationBuilder.Sql($"CREATE POLICY tenant_read ON {table} FOR SELECT USING ({ownTenant} OR ({alsoReadable}));");
        migrationBuilder.Sql($"CREATE POLICY tenant_insert ON {table} FOR INSERT WITH CHECK ({ownTenant});");
        migrationBuilder.Sql($"CREATE POLICY tenant_update ON {table} FOR UPDATE USING ({ownTenant}) WITH CHECK ({ownTenant});");
        migrationBuilder.Sql($"CREATE POLICY tenant_delete ON {table} FOR DELETE USING ({ownTenant});");
    }
}
