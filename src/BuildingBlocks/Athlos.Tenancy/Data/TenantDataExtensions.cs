using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Athlos.Tenancy.Data;

public static class TenantDataExtensions
{
    // Registers the service's DbContext with the RLS session interceptor, and a readiness check
    // (/health/ready) that the database is reachable (design §7.1).
    public static IServiceCollection AddTenantDbContext<TContext>(this IServiceCollection services, Action<DbContextOptionsBuilder> configure)
        where TContext : TenantDbContext
    {
        services.AddScoped<TenantSessionInterceptor>();
        services.AddDbContext<TContext>((sp, options) =>
        {
            configure(options);
            options.AddInterceptors(sp.GetRequiredService<TenantSessionInterceptor>());
        });

        services.AddHealthChecks().AddCheck<DatabaseReadyCheck<TContext>>("database");
        return services;
    }

    private sealed class DatabaseReadyCheck<TContext>(TContext db) : IHealthCheck
        where TContext : DbContext
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Cannot connect to the database.");
    }
}
