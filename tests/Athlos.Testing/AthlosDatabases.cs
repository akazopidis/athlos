using Athlos.Catalog.Api.Data;
using Athlos.DbMigrator;
using Athlos.Ordering.Api.Data;
using Athlos.Tenancy;
using Athlos.Tenancy.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Athlos.Testing;

// A real PostgreSQL 17 with one database per service, prepared by the same code the db-migrator
// runs on every deploy: each service's app role, migrations and RLS policies. Tests connect as the
// non-owner app roles, so RLS applies to them exactly as it does to the running services.
public sealed class AthlosDatabases : IAsyncLifetime
{
    private const string AppPassword = "test-app-password";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public static string AppRole(string service) => $"athlos_{service}_app";

    // Owner (superuser in the container). Only for arranging data that the app role may not write.
    public string OwnerConnectionString(string service) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = $"athlos_{service}" }.ConnectionString;

    public string AppConnectionString(string service) => new NpgsqlConnectionStringBuilder(OwnerConnectionString(service))
    {
        Username = AppRole(service),
        Password = AppPassword,
        MaxPoolSize = 3,
        Options = "-c statement_timeout=5000",
    }.ConnectionString;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        await Prepare<TenancyDbContext>("tenancy", options => new TenancyDbContext(options, new TenantContext()));
        await Prepare<CatalogDbContext>("catalog", options => new CatalogDbContext(options, new TenantContext()));
        await Prepare<OrderingDbContext>("ordering", options => new OrderingDbContext(options, new TenantContext()));
    }

    private async Task Prepare<TContext>(string service, Func<DbContextOptions<TContext>, TContext> createContext)
        where TContext : DbContext
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var admin = NpgsqlDataSource.Create(_container.GetConnectionString()))
        {
            await ServiceDatabase.EnsureDatabaseAsync(admin, $"athlos_{service}", cancellationToken);
        }

        await using var owner = NpgsqlDataSource.Create(OwnerConnectionString(service));
        await ServiceDatabase.PrepareAsync(owner, AppRole(service), AppPassword, createContext, cancellationToken);
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
