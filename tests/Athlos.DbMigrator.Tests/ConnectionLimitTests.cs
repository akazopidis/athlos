using Athlos.DbMigrator;
using Athlos.Testing;
using Npgsql;

namespace Athlos.DbMigrator.Tests;

// The connection budget (design §7): the migrator caps each service database, and a re-run with
// another stage size moves the cap. A database of its own, so the shared ones keep no cap.
public sealed class ConnectionLimitTests(AthlosDatabases database)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_migrator_caps_a_database_and_a_rerun_moves_the_cap()
    {
        await using var admin = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(database.OwnerConnectionString("tenancy")) { Database = "postgres" }.ConnectionString);
        await ServiceDatabase.EnsureDatabaseAsync(admin, "athlos_limit_test", Cancel);

        await ServiceDatabase.LimitConnectionsAsync(admin, "athlos_limit_test", 4, Cancel);
        var first = await Limit(admin);
        await ServiceDatabase.LimitConnectionsAsync(admin, "athlos_limit_test", 8, Cancel);
        var second = await Limit(admin);

        Assert.Equal((4, 8), (first, second));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ServiceDatabase.LimitConnectionsAsync(admin, "athlos_limit_test", -2, Cancel));
        await Assert.ThrowsAsync<ArgumentException>(() => ServiceDatabase.LimitConnectionsAsync(admin, "athlos_limit_test; DROP DATABASE x", 4, Cancel));
    }

    private static async Task<int> Limit(NpgsqlDataSource admin)
    {
        await using var command = admin.CreateCommand("SELECT datconnlimit FROM pg_database WHERE datname = 'athlos_limit_test'");
        return (int)(await command.ExecuteScalarAsync(Cancel))!;
    }
}
