using System.ComponentModel.DataAnnotations;
using Athlos.Catalog.Api.Data;
using Athlos.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Internal;

// For enrollment-api, which owns reviews (design §2.4): a program's review count and average, kept
// on the program so the shop can show and sort by them. The values are absolute (a repeated call is
// harmless). Not a cross-tenant slice: the handler enters the named tenant, so the tenant filter and
// RLS confine the update to that tenant's program; a program that is gone, or another tenant's, is
// simply not changed (204 either way, so a refund's retry never waits on a deleted program).
public static class SetProgramRating
{
    public sealed record Request([property: Range(0, int.MaxValue)] int Count, [property: Range(typeof(decimal), "0", "5")] decimal Average);

    public static void MapSetProgramRating(this IEndpointRouteBuilder app) =>
        app.MapPut("/tenants/{tenantId:guid}/programs/{programId:guid}/rating", Handle)
            .ExcludeFromDescription();

    private static async Task<IResult> Handle(
        Guid tenantId, Guid programId, Request request, TenantContext tenant, CatalogDbContext db, CancellationToken cancellationToken)
    {
        tenant.EnterTenant(tenantId, role: null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Programs
            .Where(p => p.Id == programId)
            .ExecuteUpdateAsync(p => p.SetProperty(x => x.RatingCount, request.Count).SetProperty(x => x.RatingAverage, request.Count == 0 ? 0 : request.Average),
                cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
