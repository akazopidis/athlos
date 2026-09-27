using Athlos.DbMigrator;
using Athlos.Tenancy;
using Athlos.Tenancy.Api.Data;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Athlos.Testing;

// A real PostgreSQL 17, prepared by the same code the db-migrator runs on every deploy: the
// athlos_tenancy_app role, migrations and RLS policies. Tests connect as that non-owner role, so
// RLS applies to them exactly as it does to the running service.
public sealed class TenancyDatabase : IAsyncLifetime
{
    public const string AppRole = "athlos_tenancy_app";
    private const string AppPassword = "test-app-password";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("athlos_tenancy")
        .Build();

    // Owner (superuser in the container). Only for arranging data that the app role may not write.
    public string OwnerConnectionString => _container.GetConnectionString();

    public string AppConnectionString => new NpgsqlConnectionStringBuilder(OwnerConnectionString)
    {
        Username = AppRole,
        Password = AppPassword,
        MaxPoolSize = 3,
    }.ConnectionString;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        await ServiceDatabase.PrepareAsync<TenancyDbContext>(
            OwnerConnectionString, AppRole, AppPassword,
            options => new TenancyDbContext(options, new TenantContext()),
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
