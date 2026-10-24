using Athlos.Catalog.Api.Data;
using Athlos.Media;
using Athlos.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Internal;

// Deleting a workspace (design §3.4), called by tenancy-api: every program, workout, image and shown
// discount code of one tenant. Not a cross-tenant slice: the handler enters that tenant before its
// transaction, so the tenant filter and RLS limit the deletes to its rows; the media go by their
// tenants/{id}/ prefix.
// Idempotent: a retry finds nothing left and answers 204 again.
public static class EraseTenant
{
    public static void MapEraseTenant(this IEndpointRouteBuilder app) =>
        app.MapDelete("/tenants/{tenantId:guid}", Handle)
            .ExcludeFromDescription();

    private static async Task<IResult> Handle(
        Guid tenantId, TenantContext tenant, CatalogDbContext db, MediaStore media, CancellationToken cancellationToken)
    {
        tenant.EnterTenant(tenantId, role: null);
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await db.Workouts.ExecuteDeleteAsync(cancellationToken);
            await db.Programs.ExecuteDeleteAsync(cancellationToken);
            await db.PublicDiscounts.ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // After the rows: a failure here leaves only unreachable files, and a retry removes them.
        await media.DeleteTenantAsync(tenantId, cancellationToken);
        return TypedResults.NoContent();
    }
}
