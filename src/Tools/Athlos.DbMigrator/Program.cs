using Athlos.DbMigrator;
using Athlos.Tenancy;
using Athlos.Tenancy.Api.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Runs once per deploy (a Container Apps Job in Azure, a one-shot resource under Aspire) and exits.
// Configuration per service:
//   ConnectionStrings:<service>-db-owner   owner connection (migrator / local superuser)
//   Databases:<Service>:AppRole            the service's non-owner role
//   Databases:<Service>:AppPassword        local only; Azure uses the managed identity
var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
using var host = builder.Build();

var configuration = host.Services.GetRequiredService<IConfiguration>();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Athlos.DbMigrator");
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

await ServiceDatabase.PrepareAsync<TenancyDbContext>(
    configuration.GetConnectionString("tenancy-db-owner") ?? throw new InvalidOperationException("Missing ConnectionStrings:tenancy-db-owner."),
    configuration["Databases:Tenancy:AppRole"] ?? "athlos_tenancy_app",
    configuration["Databases:Tenancy:AppPassword"],
    options => new TenancyDbContext(options, new TenantContext()),
    lifetime.ApplicationStopping);
Log.DatabaseReady(logger, "tenancy");

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "{Service} database is up to date")]
    public static partial void DatabaseReady(ILogger logger, string service);
}
