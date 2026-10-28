using Athlos.Catalog.Api.Data;
using Athlos.DbMigrator;
using Athlos.Enrollment.Api.Data;
using Athlos.Ordering.Api.Data;
using Athlos.Tenancy;
using System.Diagnostics;
using Athlos.Tenancy.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Athlos.Testing;

// A real PostgreSQL 17 with one database per service, prepared by the same code the db-migrator
// runs on every deploy: each service's app role, migrations and RLS policies. Tests connect as the
// non-owner app roles, so RLS applies to them exactly as it does to the running services.
//
// Old code on a new schema (design §12.2, backlog item 41): with ATHLOS_TEST_NEWER_MIGRATOR set to a
// newer release's built db-migrator (Athlos.DbMigrator.dll), that migrator prepares the databases first,
// so these tests (the previous release's) run against the next release's schema, as the previous
// release's apps do while a deploy rolls out or after a rollback. This code's own migrations then find
// nothing to apply.
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
        if (Environment.GetEnvironmentVariable("ATHLOS_TEST_NEWER_MIGRATOR") is { Length: > 0 } newerMigrator)
        {
            await RunMigratorAsync(newerMigrator, TestContext.Current.CancellationToken);
        }

        await Prepare<TenancyDbContext>("tenancy", options => new TenancyDbContext(options, new TenantContext()));
        await Prepare<CatalogDbContext>("catalog", options => new CatalogDbContext(options, new TenantContext()));
        await Prepare<OrderingDbContext>("ordering", options => new OrderingDbContext(options, new TenantContext()));
        await Prepare<EnrollmentDbContext>("enrollment", options => new EnrollmentDbContext(options, new TenantContext()));
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

    private async Task RunMigratorAsync(string migrator, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("dotnet", [migrator]) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var (service, section) in new[] { ("tenancy", "Tenancy"), ("catalog", "Catalog"), ("ordering", "Ordering"), ("enrollment", "Enrollment") })
        {
            start.Environment[$"ConnectionStrings__{service}-db-owner"] = OwnerConnectionString(service);
            start.Environment[$"Databases__{section}__AppRole"] = AppRole(service);
            start.Environment[$"Databases__{section}__AppPassword"] = AppPassword;
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {migrator}.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"The newer db-migrator failed ({process.ExitCode}):\n{await output}\n{await errors}");
        }
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
