using Athlos.Tenancy.Api.Data;
using Athlos.Tenancy.Api.Features.Plans;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Internal;

// Called by the gateway for every studio request (cached about 60 s there, design §3.2): maps the
// slug to a tenant and returns the caller's role in it. 404 when the tenant does not exist, is not
// active, or the user is not a member; the gateway turns all of these into the same 404.
public static class LookupMembership
{
    public sealed record Response(Guid TenantId, TenantRole Role, PlanName Plan);

    public static void MapLookupMembership(this IEndpointRouteBuilder app) =>
        app.MapGet("/internal/tenants/{slug}/members/{userId:guid}", Handle)
            .ExcludeFromDescription();

    private static async Task<IResult> Handle(
        string slug, Guid userId, TenantContext tenant, TenancyDbContext db, CancellationToken cancellationToken)
    {
        // Tenants are readable by every session; memberships only inside the tenant, so enter it first.
        var match = await db.Tenants
            .Where(t => t.Slug == slug && t.Status == TenantStatus.Active)
            .Select(t => new { t.Id, t.Plan })
            .SingleOrDefaultAsync(cancellationToken);
        if (match is null)
        {
            return TypedResults.NotFound();
        }

        tenant.EnterTenant(match.Id, role: null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var role = await db.Memberships
            .Where(m => m.UserId == userId)
            .Select(m => (TenantRole?)m.Role)
            .SingleOrDefaultAsync(cancellationToken);

        return role is { } found ? TypedResults.Ok(new Response(match.Id, found, match.Plan)) : TypedResults.NotFound();
    }
}
