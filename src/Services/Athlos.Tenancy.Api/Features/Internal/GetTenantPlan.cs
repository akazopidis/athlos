using Athlos.Tenancy.Api.Data;
using Athlos.Tenancy.Api.Features.Plans;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Internal;

// The plan limits of an active workspace (design §4): the gateway rate-limits each coach's studio and
// storefront traffic by them (cached about 60 s there), and catalog checks the program quota.
// 404 when the workspace does not exist or is not active.
public static class GetTenantPlan
{
    public sealed record Response(Guid TenantId, Plan Plan);

    public static void MapGetTenantPlan(this IEndpointRouteBuilder app) =>
        app.MapGet("/internal/tenants/{slug}/plan", Handle)
            .ExcludeFromDescription();

    private static async Task<IResult> Handle(string slug, TenancyDbContext db, CancellationToken cancellationToken)
    {
        // Tenants are readable by every session (RLS tenants_read); the plan is not secret.
        var match = await db.Tenants
            .Where(t => t.Slug == slug && t.Status == TenantStatus.Active)
            .Select(t => new { t.Id, t.Plan })
            .SingleOrDefaultAsync(cancellationToken);

        return match is null ? TypedResults.NotFound() : TypedResults.Ok(new Response(match.Id, Plans.Plans.For(match.Plan)));
    }
}
