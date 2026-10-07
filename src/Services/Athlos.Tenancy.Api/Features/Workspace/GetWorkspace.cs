using Athlos.Tenancy.Api.Data;
using Athlos.Tenancy.Api.Features.Plans;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Workspace;

// Workspace settings for the coach dashboard. The tenant comes from the gateway (it resolved the
// slug and checked membership); the slug in the route must match it, or it is someone else's: 404.
public static class GetWorkspace
{
    public sealed record Response(
        Guid Id, string Slug, string DisplayName, TenantStatus Status, PlanName Plan, Branding Branding, bool StripeOnboardingComplete);

    public static void MapGetWorkspace(this IEndpointRouteBuilder app) =>
        app.MapGet("/workspace", Handle)
            .WithName("GetWorkspace")
            .WithTags("Workspace");

    private static async Task<Results<Ok<Response>, NotFound>> Handle(string tenantSlug, ITenantContext tenant, TenancyDbContext db, CancellationToken cancellationToken)
    {
        var workspace = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenant.TenantId && t.Slug == tenantSlug && t.Status != TenantStatus.Deleted)
            .Select(t => new Response(t.Id, t.Slug, t.DisplayName, t.Status, t.Plan, t.Branding, t.StripeOnboardingComplete))
            .SingleOrDefaultAsync(cancellationToken);

        return workspace is null ? TypedResults.NotFound() : TypedResults.Ok(workspace);
    }
}
