using Athlos.Catalog.Api.Data;
using Athlos.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Internal;

// Suspending or reactivating a workspace (design §3.4), called by tenancy-api for the platform admin:
// the coach's programs leave the marketplace, or come back. Customers keep what they bought
// (GetProgramContent does not look at the flag). Not a cross-tenant slice: the handler enters that
// tenant before its transaction, so the tenant filter and RLS limit the update to its rows.
// Idempotent: a retry sets the same value again.
public static class SuspendTenant
{
    public sealed record Request(bool Suspended);

    public static void MapSuspendTenant(this IEndpointRouteBuilder app) =>
        app.MapPut("/tenants/{tenantId:guid}/suspension", Handle)
            .ExcludeFromDescription();

    private static async Task<IResult> Handle(
        Guid tenantId, Request request, TenantContext tenant, CatalogDbContext db, CancellationToken cancellationToken)
    {
        tenant.EnterTenant(tenantId, role: null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Programs.ExecuteUpdateAsync(p => p.SetProperty(x => x.TenantSuspended, request.Suspended), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
