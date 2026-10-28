using Athlos.DbMigrator;
using Athlos.Tenancy;
using Athlos.Tenancy.Api.Data;
using Athlos.Testing;
using Npgsql;

namespace Athlos.DbMigrator.Tests;

// The app role the migrator prepares (design §3.2 pitfalls, §11): it can never bypass RLS, owns
// nothing, and gets only DML on its own database. Each test has a database and roles of its own
// (roles are server-wide), so the shared databases are untouched.
public sealed class RoleSetupTests(AthlosDatabases database)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_app_role_logs_in_without_any_power_and_gets_only_DML()
    {
        var (db, role) = Names("fresh");
        await Prepare(db, role, password: "pw");

        await using var admin = Admin();
        Assert.Equal((false, false, false, false, true), await Scalar<(bool, bool, bool, bool, bool)>(admin,
            $"SELECT rolsuper, rolbypassrls, rolcreatedb, rolcreaterole, rolcanlogin FROM pg_roles WHERE rolname = '{role}'",
            r => (r.GetBoolean(0), r.GetBoolean(1), r.GetBoolean(2), r.GetBoolean(3), r.GetBoolean(4))));

        await using var owner = Owner(db);
        Assert.True(await Scalar<bool>(owner, $"SELECT has_table_privilege('{role}', 'tenants', 'SELECT, INSERT, UPDATE, DELETE')"));
        Assert.False(await Scalar<bool>(owner, $"SELECT has_table_privilege('{role}', 'tenants', 'TRUNCATE')"));
        Assert.True(await Scalar<bool>(owner, $"""SELECT has_table_privilege('{role}', '"__EFMigrationsHistory"', 'SELECT')"""));
        Assert.False(await Scalar<bool>(owner, $"""SELECT has_table_privilege('{role}', '"__EFMigrationsHistory"', 'INSERT, UPDATE, DELETE')"""));
        Assert.False(await Scalar<bool>(owner, $"SELECT has_schema_privilege('{role}', 'public', 'CREATE')"));
        Assert.Equal(0L, await Scalar<long>(owner, $"SELECT count(*) FROM pg_class WHERE relowner = (SELECT oid FROM pg_roles WHERE rolname = '{role}')"));
    }

    [Fact]
    public async Task Only_the_app_role_may_connect_to_its_database()
    {
        var (db, role) = Names("connect");
        await Prepare(db, role, password: "pw");
        var stranger = $"{role}_stranger";
        await using var admin = Admin();
        await Execute(admin, $"CREATE ROLE {stranger} LOGIN");

        Assert.True(await Scalar<bool>(admin, $"SELECT has_database_privilege('{role}', '{db}', 'CONNECT')"));
        Assert.False(await Scalar<bool>(admin, $"SELECT has_database_privilege('{stranger}', '{db}', 'CONNECT')"));
    }

    [Theory]
    [InlineData("BYPASSRLS")]
    [InlineData("SUPERUSER")]
    public async Task A_role_made_elsewhere_that_could_skip_RLS_stops_the_migration(string power)
    {
        var (db, role) = Names(power.ToLowerInvariant());
        await using (var admin = Admin())
        {
            await ServiceDatabase.EnsureDatabaseAsync(admin, db, Cancel);
            await Execute(admin, $"CREATE ROLE {role} LOGIN {power}");
        }

        // As in Azure, where the role is the service's managed identity and no password is set.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Prepare(db, role, password: null));

        Assert.Contains(role, refused.Message, StringComparison.Ordinal);
        await using var owner = Owner(db);
        Assert.False(await Scalar<bool>(owner, "SELECT to_regclass('tenants') IS NOT NULL"));
    }

    [Fact]
    public async Task A_rerun_takes_a_power_back_from_the_password_role_and_adds_nothing()
    {
        var (db, role) = Names("rerun");
        await Prepare(db, role, password: "pw");
        await using var owner = Owner(db);
        var migrations = await Scalar<long>(owner, """SELECT count(*) FROM "__EFMigrationsHistory" """);
        await using (var admin = Admin())
        {
            await Execute(admin, $"ALTER ROLE {role} BYPASSRLS CREATEDB");
        }

        await Prepare(db, role, password: "pw");

        await using var check = Admin();
        Assert.Equal((false, false), await Scalar<(bool, bool)>(check,
            $"SELECT rolbypassrls, rolcreatedb FROM pg_roles WHERE rolname = '{role}'", r => (r.GetBoolean(0), r.GetBoolean(1))));
        Assert.Equal(migrations, await Scalar<long>(owner, """SELECT count(*) FROM "__EFMigrationsHistory" """));
    }

    [Fact]
    public async Task A_name_that_is_not_a_plain_identifier_is_refused()
    {
        var (db, _) = Names("names");
        await using (var admin = Admin())
        {
            await ServiceDatabase.EnsureDatabaseAsync(admin, db, Cancel);
            await Assert.ThrowsAsync<ArgumentException>(() => ServiceDatabase.EnsureDatabaseAsync(admin, "x; DROP DATABASE postgres", Cancel));
        }

        await Assert.ThrowsAsync<ArgumentException>(() => Prepare(db, "app; ALTER ROLE postgres NOLOGIN", password: "pw"));
    }

    // Unique per test run: roles outlive a database, and the container is shared by the whole assembly.
    private static (string Database, string Role) Names(string test)
    {
        var suffix = $"{test}_{Guid.CreateVersion7().ToString("N")[^8..]}";
        return ($"athlos_roles_{suffix}", $"athlos_roles_{suffix}_app");
    }

    private async Task Prepare(string db, string role, string? password)
    {
        await using (var admin = Admin())
        {
            await ServiceDatabase.EnsureDatabaseAsync(admin, db, Cancel);
        }

        await using var owner = Owner(db);
        await ServiceDatabase.PrepareAsync<TenancyDbContext>(owner, role, password, options => new TenancyDbContext(options, new TenantContext()), Cancel);
    }

    private NpgsqlDataSource Admin() => Owner("postgres");

    private NpgsqlDataSource Owner(string db) =>
        NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(database.OwnerConnectionString("tenancy")) { Database = db }.ConnectionString);

    private static async Task Execute(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Cancel);
    }

    private static async Task<T> Scalar<T>(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(Cancel))!;
    }

    private static async Task<T> Scalar<T>(NpgsqlDataSource source, string sql, Func<NpgsqlDataReader, T> read)
    {
        await using var command = source.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        Assert.True(await reader.ReadAsync(Cancel));
        return read(reader);
    }
}
