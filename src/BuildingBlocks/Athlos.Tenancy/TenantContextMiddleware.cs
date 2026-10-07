using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Athlos.Tenancy;

public static class TenantContextMiddleware
{
    // Entra puts the user's object id in "oid"; ASP.NET Core may map it to the long claim type.
    private const string ObjectIdClaim = "oid";
    private const string ObjectIdClaimLong = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    public static IServiceCollection AddTenantContext(this IServiceCollection services)
    {
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        return services;
    }

    // After UseGatewayOnly (so the headers are the gateway's) and UseAuthentication (so the user is known).
    public static IApplicationBuilder UseTenantContext(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Request.Headers;
            var tenantContext = context.RequestServices.GetRequiredService<TenantContext>();

            tenantContext.Populate(
                Guid.TryParse(headers[TrustedHeaders.TenantId], out var tenantId) ? tenantId : null,
                UserIdOf(context.User),
                Enum.TryParse<TenantRole>(headers[TrustedHeaders.UserRole], ignoreCase: false, out var role) ? role : null);

            // Tenant-aware telemetry (design §6): the request's span and every log line of the
            // request carry the tenant and role, so App Insights can be filtered by coach. No user
            // ids: they identify a person.
            if (tenantContext.TenantId is null)
            {
                await next(context);
                return;
            }

            Activity.Current?.SetTag(TelemetryTags.TenantId, tenantContext.TenantId.ToString());
            Activity.Current?.SetTag(TelemetryTags.UserRole, tenantContext.Role?.ToString());
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Athlos.Tenancy");
            using (logger.BeginScope(new Dictionary<string, object?>
            {
                [TelemetryTags.TenantId] = tenantContext.TenantId.ToString(),
                [TelemetryTags.UserRole] = tenantContext.Role?.ToString(),
            }))
            {
                await next(context);
            }
        });

    public static Guid? UserIdOf(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var value = user.FindFirstValue(ObjectIdClaim) ?? user.FindFirstValue(ObjectIdClaimLong);
        return Guid.TryParse(value, out var id) ? id : null;
    }
}
