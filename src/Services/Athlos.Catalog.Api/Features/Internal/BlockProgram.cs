using Athlos.Catalog.Api.Data;
using Athlos.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Internal;

// The platform admin takes a reported program off the marketplace, or allows it again (design §2.4),
// through tenancy-api, which keeps the reports and tells the coach the reason. Blocking unpublishes
// the program and keeps the coach from publishing it again (PublishProgram); allowing only lifts the
// block, and the coach publishes it again if they want. Customers keep what they bought. The handler
// enters the program's tenant, so the tenant filter and RLS confine the change to it: another
// tenant's program is 404. Idempotent.
public static class BlockProgram
{
    public sealed record Request(bool Blocked);

    public static void MapBlockProgram(this IEndpointRouteBuilder app) =>
        app.MapPut("/tenants/{tenantId:guid}/programs/{programId:guid}/block", Handle)
            .ExcludeFromDescription();

    private static async Task<IResult> Handle(
        Guid tenantId, Guid programId, Request request, TenantContext tenant, CatalogDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        tenant.EnterTenant(tenantId, role: null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var program = await db.Programs.SingleOrDefaultAsync(p => p.Id == programId, cancellationToken);
        if (program is null)
        {
            return TypedResults.NotFound();
        }

        if (request.Blocked)
        {
            program.BlockedAt ??= time.GetUtcNow();
            program.IsPublished = false;
            program.PublishedAt = null;
        }
        else
        {
            program.BlockedAt = null;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
