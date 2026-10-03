using Athlos.Tenancy.Api.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Internal;

// For the notifications-worker (signed service-to-service call): where to send a workspace's
// "you made a sale" email (design §5.3). Only tenancy knows who the owners are and their addresses,
// so the sale email names the tenant and the worker asks here when it sends.
// The handler enters that one tenant; RLS then shows its memberships and the profiles of its members
// (user_profiles policy tenant_members_read), nothing of other tenants.
public static class GetOwnerEmails
{
    public sealed record Response(Guid TenantId, List<string> Emails);

    public static void MapGetOwnerEmails(this IEndpointRouteBuilder app) =>
        app.MapGet("/internal/tenants/{tenantId:guid}/owner-emails", Handle)
            .ExcludeFromDescription();

    private static async Task<Results<Ok<Response>, NotFound>> Handle(
        Guid tenantId, TenantContext tenant, TenancyDbContext db, CancellationToken cancellationToken)
    {
        tenant.EnterTenant(tenantId, role: null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var emails = await db.Memberships.AsNoTracking()
            .Where(m => m.Role == TenantRole.Owner)
            .Join(db.UserProfiles, m => m.UserId, p => p.UserId, (m, p) => p.Email)
            .Distinct()
            .ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return TypedResults.Ok(new Response(tenantId, emails));
    }
}
