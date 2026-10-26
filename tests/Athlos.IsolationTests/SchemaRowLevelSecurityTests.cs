using Athlos.Testing;
using Npgsql;

namespace Athlos.IsolationTests;

// Layer 4 for the whole schema (design §3.2): after the same migrations the db-migrator runs, every
// table in every service database has RLS enabled, forced and at least one policy. A migration that
// forgets them fails here, not in production. The tables below are the only exceptions, each with its
// reason; none of them may hold a tenant_id.
public sealed class SchemaRowLevelSecurityTests(AthlosDatabases databases)
{
    private static readonly Dictionary<string, string> WithoutRls = new()
    {
        ["__EFMigrationsHistory"] = "EF's list of applied migrations, in every database; no tenant or user data",
    };

    public static TheoryData<string> Services => new() { "tenancy", "catalog", "ordering", "enrollment" };

    [Theory]
    [MemberData(nameof(Services))]
    public async Task Every_table_has_RLS_enabled_forced_and_a_policy(string service)
    {
        var tables = await Tables(service);

        var unprotected = tables
            .Where(t => !WithoutRls.ContainsKey(t.Name) && !(t.Enabled && t.Forced && t.Policies > 0))
            .Select(t => $"{t.Name} (enabled {t.Enabled}, forced {t.Forced}, policies {t.Policies})");
        var exemptButTenantOwned = tables.Where(t => WithoutRls.ContainsKey(t.Name) && t.HasTenantId).Select(t => t.Name);

        Assert.Empty(unprotected);
        Assert.Empty(exemptButTenantOwned);
    }

    [Fact]
    public async Task Every_exception_still_names_a_table()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var service in Services)
        {
            names.UnionWith((await Tables(service)).Select(t => t.Name));
        }

        Assert.Subset(names, WithoutRls.Keys.ToHashSet(StringComparer.Ordinal));
    }

    private sealed record Table(string Name, bool Enabled, bool Forced, long Policies, bool HasTenantId);

    private async Task<List<Table>> Tables(string service)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(databases.OwnerConnectionString(service));
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity,
                   (SELECT count(*) FROM pg_policy p WHERE p.polrelid = c.oid),
                   EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped)
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')
            ORDER BY c.relname
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tables = new List<Table>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(new Table(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetInt64(3), reader.GetBoolean(4)));
        }

        Assert.NotEmpty(tables);
        return tables;
    }
}
