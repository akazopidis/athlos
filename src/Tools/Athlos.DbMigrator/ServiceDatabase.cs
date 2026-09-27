using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Athlos.DbMigrator;

// Prepares one service's database (design §3.2 pitfalls, §11):
// 1. the database exists, owned by the migrator;
// 2. the service's app role exists (a password role locally, the service's managed identity in
//    Azure) and can never bypass RLS;
// 3. migrations (tables, indexes, RLS policies) run as the owner;
// 4. the app role gets only DML on its own database.
public static partial class ServiceDatabase
{
    // Connected to the server's "postgres" database as the migrator (Entra admin in Azure).
    public static async Task EnsureDatabaseAsync(NpgsqlDataSource admin, string database, CancellationToken cancellationToken)
    {
        RequireIdentifier(database);
        var exists = await ScalarAsync<bool>(admin, $"SELECT EXISTS (SELECT FROM pg_database WHERE datname = '{database}')", cancellationToken);
        if (!exists)
        {
            await ExecuteAsync(admin, $"CREATE DATABASE {database};", cancellationToken);
        }
    }

    // Azure only: maps the service's managed identity to a PostgreSQL role with the Athlos name
    // (athlos_{service}_{env}_app), so tokens for that identity sign in as that role.
    public static async Task EnsureEntraPrincipalAsync(NpgsqlDataSource admin, string appRole, Guid principalObjectId, CancellationToken cancellationToken)
    {
        RequireIdentifier(appRole);
        var exists = await ScalarAsync<bool>(admin, $"SELECT EXISTS (SELECT FROM pg_roles WHERE rolname = '{appRole}')", cancellationToken);
        if (!exists)
        {
            await ExecuteAsync(admin,
                $"SELECT * FROM pgaadauth_create_principal_with_oid('{appRole}', '{principalObjectId}', 'service', false, false);",
                cancellationToken);
        }
    }

    // Connected to the service's own database as its owner.
    public static async Task PrepareAsync<TContext>(
        NpgsqlDataSource owner,
        string appRole,
        string? appPassword,
        Func<DbContextOptions<TContext>, TContext> createContext,
        CancellationToken cancellationToken)
        where TContext : DbContext
    {
        RequireIdentifier(appRole);
        var database = new NpgsqlConnectionStringBuilder(owner.ConnectionString).Database
            ?? throw new ArgumentException("The owner connection has no database.", nameof(owner));

        // Local: a password role that this migrator creates and therefore controls.
        if (appPassword is not null)
        {
            await ExecuteAsync(owner, $"""
                DO $$ BEGIN
                  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '{appRole}') THEN
                    CREATE ROLE {appRole} LOGIN;
                  END IF;
                END $$;
                ALTER ROLE {appRole} WITH LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE PASSWORD '{appPassword.Replace("'", "''", StringComparison.Ordinal)}';
                """, cancellationToken);
        }

        // However the role was created, refuse to continue if it could skip RLS.
        var canBypass = await ScalarAsync<bool>(owner,
            $"SELECT rolsuper OR rolbypassrls FROM pg_roles WHERE rolname = '{appRole}'", cancellationToken);
        if (canBypass)
        {
            throw new InvalidOperationException($"Role {appRole} is a superuser or has BYPASSRLS; RLS would not apply to it.");
        }

        var options = new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(owner)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var db = createContext(options))
        {
            await db.Database.MigrateAsync(cancellationToken);
        }

        await ExecuteAsync(owner, $"""
            REVOKE ALL ON DATABASE "{database}" FROM PUBLIC;
            GRANT CONNECT ON DATABASE "{database}" TO {appRole};
            GRANT USAGE ON SCHEMA public TO {appRole};
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {appRole};
            REVOKE INSERT, UPDATE, DELETE ON "__EFMigrationsHistory" FROM {appRole};
            """, cancellationToken);
    }

    private static void RequireIdentifier(string name)
    {
        if (!Identifier().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not a plain PostgreSQL identifier.", nameof(name));
        }
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex Identifier();
}
