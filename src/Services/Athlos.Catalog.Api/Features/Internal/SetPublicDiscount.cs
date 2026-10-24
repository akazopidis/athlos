using System.ComponentModel.DataAnnotations;
using Athlos.Catalog.Api.Data;
using Athlos.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Internal;

// For ordering-api, which owns discount codes (design §2.4): the code a workspace shows on its
// programs, or none. Absolute values, so a repeated call is harmless. Not a cross-tenant slice: the
// handler enters the named tenant, so the tenant filter and RLS confine the write to that tenant's row.
public static class SetPublicDiscount
{
    public sealed record Request(
        [property: Required, RegularExpression("^[A-Z0-9-]{3,20}$")] string Code,
        [property: Range(5, 90)] int PercentOff,
        Guid? ProgramId,
        DateTimeOffset? ExpiresAt);

    public static void MapSetPublicDiscount(this IEndpointRouteBuilder app)
    {
        app.MapPut("/tenants/{tenantId:guid}/public-discount", Set).ExcludeFromDescription();
        app.MapDelete("/tenants/{tenantId:guid}/public-discount", Clear).ExcludeFromDescription();
    }

    private static async Task<IResult> Set(Guid tenantId, Request request, TenantContext tenant, CatalogDbContext db, CancellationToken cancellationToken)
    {
        tenant.EnterTenant(tenantId, role: null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.PublicDiscounts.ExecuteDeleteAsync(cancellationToken);
        db.PublicDiscounts.Add(new PublicDiscount
        {
            Code = request.Code,
            PercentOff = request.PercentOff,
            ProgramId = request.ProgramId,
            ExpiresAt = request.ExpiresAt,
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> Clear(Guid tenantId, TenantContext tenant, CatalogDbContext db, CancellationToken cancellationToken)
    {
        tenant.EnterTenant(tenantId, role: null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.PublicDiscounts.ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
