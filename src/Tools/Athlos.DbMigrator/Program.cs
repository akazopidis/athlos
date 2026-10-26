using Athlos.Catalog.Api.Data;
using Athlos.DbMigrator;
using Athlos.Enrollment.Api.Data;
using Athlos.Ordering.Api.Data;
using Athlos.Tenancy;
using Athlos.Tenancy.Api.Data;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

// Runs once per deploy (a Container Apps Job in Azure, a one-shot resource under Aspire) and exits.
// Configuration per service (<service> = tenancy, catalog, ...):
//   ConnectionStrings:<service>-db-owner       the service's database, as the migrator
//   Databases:<Service>:AppRole                 the service's non-owner role
//   Databases:<Service>:AppPassword             local only
//   Databases:<Service>:AppPrincipalObjectId    Azure only: the service's managed identity
//   AZURE_CLIENT_ID                             Azure only: the migrator's managed identity
//   Databases:ConnectionLimit                   Azure only: the cap per database (design §7); none locally
//   Seed:DemoData                               true in dev and staging: add the demo data (design §15.2)
var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
using var host = builder.Build();

var configuration = host.Services.GetRequiredService<IConfiguration>();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Athlos.DbMigrator");
var cancellationToken = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
var managedIdentityClientId = configuration["AZURE_CLIENT_ID"];

await Migrate<TenancyDbContext>("tenancy", "Tenancy", options => new TenancyDbContext(options, new TenantContext()));
await Migrate<CatalogDbContext>("catalog", "Catalog", options => new CatalogDbContext(options, new TenantContext()));
await Migrate<OrderingDbContext>("ordering", "Ordering", options => new OrderingDbContext(options, new TenantContext()));
await Migrate<EnrollmentDbContext>("enrollment", "Enrollment", options => new EnrollmentDbContext(options, new TenantContext()));

if (configuration.GetValue<bool>("Seed:DemoData"))
{
    await using var tenancy = DataSource(OwnerConnectionString("tenancy"));
    await using var catalog = DataSource(OwnerConnectionString("catalog"));
    await using var ordering = DataSource(OwnerConnectionString("ordering"));
    await using var enrollment = DataSource(OwnerConnectionString("enrollment"));
    var skipped = await DemoData.SeedAsync(new DemoData.Sources(tenancy, catalog, ordering, enrollment), cancellationToken);
    foreach (var slug in skipped)
    {
        Log.DemoCoachSkipped(logger, slug);
    }

    Log.DemoDataReady(logger);
}

async Task Migrate<TContext>(string service, string section, Func<DbContextOptions<TContext>, TContext> createContext)
    where TContext : DbContext
{
    var ownerConnectionString = OwnerConnectionString(service);
    var appRole = configuration[$"Databases:{section}:AppRole"] ?? $"athlos_{service}_app";
    var database = new NpgsqlConnectionStringBuilder(ownerConnectionString).Database!;

    await using (var admin = DataSource(new NpgsqlConnectionStringBuilder(ownerConnectionString) { Database = "postgres" }.ConnectionString))
    {
        await ServiceDatabase.EnsureDatabaseAsync(admin, database, cancellationToken);

        if (configuration.GetValue<int?>("Databases:ConnectionLimit") is { } limit)
        {
            await ServiceDatabase.LimitConnectionsAsync(admin, database, limit, cancellationToken);
        }

        if (Guid.TryParse(configuration[$"Databases:{section}:AppPrincipalObjectId"], out var principalObjectId))
        {
            await ServiceDatabase.EnsureEntraPrincipalAsync(admin, appRole, principalObjectId, cancellationToken);
        }
    }

    await using (var owner = DataSource(ownerConnectionString))
    {
        await ServiceDatabase.PrepareAsync(owner, appRole, configuration[$"Databases:{section}:AppPassword"], createContext, cancellationToken);
    }

    Log.DatabaseReady(logger, database);
}

string OwnerConnectionString(string service) =>
    configuration.GetConnectionString($"{service}-db-owner")
        ?? throw new InvalidOperationException($"Missing ConnectionStrings:{service}-db-owner.");

NpgsqlDataSource DataSource(string connectionString) =>
    new NpgsqlDataSourceBuilder(connectionString).UseEntraTokenIfNoPassword(managedIdentityClientId).Build();

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "{Database} is up to date")]
    public static partial void DatabaseReady(ILogger logger, string database);

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo data is in place")]
    public static partial void DemoDataReady(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo coach {Slug} skipped: another workspace has that address")]
    public static partial void DemoCoachSkipped(ILogger logger, string slug);
}
