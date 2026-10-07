using Athlos.DbMigrator;
using Athlos.Testing;
using Npgsql;

[assembly: AssemblyFixture(typeof(AthlosDatabases))]

namespace Athlos.DbMigrator.Tests;

// The demo data (design §15.2): what k6 and the demos rely on, seeded twice to prove a re-run
// (every dev and staging deploy) adds nothing and never touches a real workspace.
public sealed class DemoDataTests(AthlosDatabases database)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Seeds_the_demo_coaches_once_and_skips_an_address_that_is_taken()
    {
        // A real workspace that took demo-flow before demo-* was reserved.
        var realTenant = Guid.CreateVersion7();
        await Owner("tenancy", $"""
            INSERT INTO tenants (id, slug, display_name, status, plan, branding, stripe_onboarding_complete, created_at, updated_at)
            VALUES ('{realTenant}', 'demo-flow', 'Someone else', 'Active', 'Free', '{"{}"}', false, now(), now());
            """);

        IReadOnlyList<string> skipped = [];
        for (var run = 0; run < 2; run++)
        {
            await using var tenancy = NpgsqlDataSource.Create(database.OwnerConnectionString("tenancy"));
            await using var catalog = NpgsqlDataSource.Create(database.OwnerConnectionString("catalog"));
            await using var ordering = NpgsqlDataSource.Create(database.OwnerConnectionString("ordering"));
            await using var enrollment = NpgsqlDataSource.Create(database.OwnerConnectionString("enrollment"));
            skipped = await DemoData.SeedAsync(new DemoData.Sources(tenancy, catalog, ordering, enrollment), Cancel);
        }

        Assert.Equal(["demo-flow"], skipped);
        Assert.Equal("Someone else", await Owner<string>("tenancy", "SELECT display_name FROM tenants WHERE slug = 'demo-flow'"));
        Assert.Equal("Pro", await Owner<string>("tenancy", "SELECT plan FROM tenants WHERE slug = 'demo-forge'"));
        Assert.Equal(2L, await Owner<long>("tenancy", $"SELECT count(*) FROM memberships WHERE tenant_id IN ('{DemoData.Id("tenant:demo-forge")}', '{DemoData.Id("tenant:demo-stride")}')"));
        Assert.Equal(15L, await Owner<long>("catalog", "SELECT count(*) FROM programs WHERE coach_slug LIKE 'demo-%'"));
        Assert.Equal(45L, await Owner<long>("catalog", "SELECT count(*) FROM workouts"));
        Assert.Equal(3L, await Owner<long>("ordering", "SELECT count(*) FROM orders WHERE status = 'Paid'"));
        Assert.Equal(3L, await Owner<long>("enrollment", "SELECT count(*) FROM enrollments"));
        Assert.Equal(3L, await Owner<long>("enrollment", "SELECT count(*) FROM workout_progress"));

        // The marketplace sees the published demo programs through RLS (public-read), as catalog-api does.
        Assert.Equal(15L, await AsCatalogApp("public-read"));
        Assert.Equal(0L, await AsCatalogApp(""));

        // A demo coach deleted from the studio (design §3.4) is not seeded again on the next deploy.
        await Owner("tenancy", $"UPDATE tenants SET status = 'Deleted' WHERE id = '{DemoData.Id("tenant:demo-stride")}'");
        await Owner("catalog", "DELETE FROM workouts WHERE tenant_id IN (SELECT tenant_id FROM programs WHERE coach_slug = 'demo-stride'); DELETE FROM programs WHERE coach_slug = 'demo-stride';");
        await using (var tenancy = NpgsqlDataSource.Create(database.OwnerConnectionString("tenancy")))
        await using (var catalog = NpgsqlDataSource.Create(database.OwnerConnectionString("catalog")))
        await using (var ordering = NpgsqlDataSource.Create(database.OwnerConnectionString("ordering")))
        await using (var enrollment = NpgsqlDataSource.Create(database.OwnerConnectionString("enrollment")))
        {
            skipped = await DemoData.SeedAsync(new DemoData.Sources(tenancy, catalog, ordering, enrollment), Cancel);
        }

        Assert.Equal(["demo-stride", "demo-flow"], skipped);
        Assert.Equal(0L, await Owner<long>("catalog", "SELECT count(*) FROM programs WHERE coach_slug = 'demo-stride'"));
    }

    private async Task Owner(string service, string sql)
    {
        await using var connection = new NpgsqlConnection(database.OwnerConnectionString(service));
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Cancel);
    }

    private async Task<T> Owner<T>(string service, string sql)
    {
        await using var connection = new NpgsqlConnection(database.OwnerConnectionString(service));
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Cancel))!;
    }

    // Catalog's own role, in a transaction with only app.scope set (no tenant).
    private async Task<long> AsCatalogApp(string scope)
    {
        await using var connection = new NpgsqlConnection(database.AppConnectionString("catalog"));
        await connection.OpenAsync(Cancel);
        await using var transaction = await connection.BeginTransactionAsync(Cancel);
        await using (var set = new NpgsqlCommand("SELECT set_config('app.scope', @scope, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("scope", scope);
            await set.ExecuteNonQueryAsync(Cancel);
        }

        await using var count = new NpgsqlCommand("SELECT count(*) FROM programs WHERE coach_slug LIKE 'demo-%'", connection, transaction);
        return (long)(await count.ExecuteScalarAsync(Cancel))!;
    }
}
