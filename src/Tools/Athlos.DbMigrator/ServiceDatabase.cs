using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Athlos.DbMigrator;

// Prepares one service's database (design §3.2 pitfalls, §11):
// 1. the service's app role exists and can never bypass RLS;
// 2. migrations (tables, indexes, RLS policies) run as the owner;
// 3. the app role gets only DML on its own database, and a statement timeout (design §4).
public static partial class ServiceDatabase
{
    public static async Task PrepareAsync<TContext>(
        string ownerConnectionString,
        string appRole,
        string? appPassword,
        Func<DbContextOptions<TContext>, TContext> createContext,
        CancellationToken cancellationToken)
        where TContext : DbContext
    {
        if (!RoleName().IsMatch(appRole))
        {
            throw new ArgumentException($"Invalid role name '{appRole}'.", nameof(appRole));
        }

        var database = new NpgsqlConnectionStringBuilder(ownerConnectionString).Database
            ?? throw new ArgumentException("The owner connection string has no database.", nameof(ownerConnectionString));

        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);

        // Locally the role has a password. In Azure it is the service's managed identity, created
        // with pgaadauth_create_principal before this runs (design §11), so there is no password.
        if (appPassword is not null)
        {
            await ExecuteAsync(connection, $"""
                DO $$ BEGIN
                  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '{appRole}') THEN
                    CREATE ROLE {appRole} LOGIN;
                  END IF;
                END $$;
                ALTER ROLE {appRole} WITH LOGIN PASSWORD '{appPassword.Replace("'", "''", StringComparison.Ordinal)}';
                """, cancellationToken);
        }

        // Whatever created the role, it must not skip RLS.
        await ExecuteAsync(connection,
            $"ALTER ROLE {appRole} WITH NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;", cancellationToken);

        var options = new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(ownerConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var db = createContext(options))
        {
            await db.Database.MigrateAsync(cancellationToken);
        }

        await ExecuteAsync(connection, $"""
            REVOKE ALL ON DATABASE "{database}" FROM PUBLIC;
            GRANT CONNECT ON DATABASE "{database}" TO {appRole};
            GRANT USAGE ON SCHEMA public TO {appRole};
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {appRole};
            REVOKE INSERT, UPDATE, DELETE ON "__EFMigrationsHistory" FROM {appRole};
            ALTER ROLE {appRole} SET statement_timeout = '5s';
            """, cancellationToken);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex RoleName();
}
