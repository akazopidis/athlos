using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Athlos.Tenancy.Data;

public static class TenantTransaction
{
    // Runs each endpoint in the group inside one database transaction, which TenantSessionInterceptor
    // tags with the caller's tenant and user. Commits unless the endpoint returns an error status.
    public static RouteGroupBuilder WithTenantTransaction<TDbContext>(this RouteGroupBuilder group)
        where TDbContext : DbContext
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            var db = context.HttpContext.RequestServices.GetRequiredService<TDbContext>();
            var cancellationToken = context.HttpContext.RequestAborted;

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var result = await next(context);

            if (result is not IStatusCodeHttpResult { StatusCode: >= 400 })
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return result;
        });

        return group;
    }

    // Studio routes: the gateway must have resolved the tenant and the caller's role. If it did not,
    // the resource does not exist for this caller: 404, never 403 (design §3.3).
    public static RouteGroupBuilder RequireTenant(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            var tenant = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            return tenant.TenantId is null || tenant.Role is null
                ? TypedResults.NotFound()
                : await next(context);
        });

        return group;
    }
}
