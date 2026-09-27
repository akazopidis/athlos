using Athlos.DbMigrator;
using Athlos.Tenancy;
using Athlos.Tenancy.Api.Data;
using Athlos.Tenancy.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

// Runs once per deploy (a Container Apps Job in Azure, a one-shot resource under Aspire) and exits.
// Configuration per service:
//   ConnectionStrings:<service>-db-owner       the service's database, as the migrator
//   Databases:<Service>:AppRole                 the service's non-owner role
//   Databases:<Service>:AppPassword             local only
//   Databases:<Service>:AppPrincipalObjectId    Azure only: the service's managed identity
//   AZURE_CLIENT_ID                             Azure only: the migrator's managed identity
var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
using var host = builder.Build();

var configuration = host.Services.GetRequiredService<IConfiguration>();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Athlos.DbMigrator");
var cancellationToken = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
var managedIdentityClientId = configuration["AZURE_CLIENT_ID"];

NpgsqlDataSource DataSource(string connectionString) =>
    new NpgsqlDataSourceBuilder(connectionString).UseEntraTokenIfNoPassword(managedIdentityClientId).Build();

var ownerConnectionString = configuration.GetConnectionString("tenancy-db-owner")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:tenancy-db-owner.");
var appRole = configuration["Databases:Tenancy:AppRole"] ?? "athlos_tenancy_app";
var database = new NpgsqlConnectionStringBuilder(ownerConnectionString).Database!;

await using (var admin = DataSource(new NpgsqlConnectionStringBuilder(ownerConnectionString) { Database = "postgres" }.ConnectionString))
{
    await ServiceDatabase.EnsureDatabaseAsync(admin, database, cancellationToken);

    if (Guid.TryParse(configuration["Databases:Tenancy:AppPrincipalObjectId"], out var principalObjectId))
    {
        await ServiceDatabase.EnsureEntraPrincipalAsync(admin, appRole, principalObjectId, cancellationToken);
    }
}

await using (var owner = DataSource(ownerConnectionString))
{
    await ServiceDatabase.PrepareAsync<TenancyDbContext>(
        owner, appRole, configuration["Databases:Tenancy:AppPassword"],
        options => new TenancyDbContext(options, new TenantContext()),
        cancellationToken);
}

Log.DatabaseReady(logger, database);

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "{Database} is up to date")]
    public static partial void DatabaseReady(ILogger logger, string database);
}
