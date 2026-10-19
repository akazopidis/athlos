using Athlos.Catalog.Api.Data;
using Athlos.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Internal;

// Deleting a customer's account (design §5.2), called by tenancy-api: their saved programs go. The
// rows are the customer's own, not a tenant's: the handler acts for that user, so RLS
// (customer_id = app.user_id) confines the delete to their rows. Idempotent.
public static class EraseCustomer
{
    public static void MapEraseCustomer(this IEndpointRouteBuilder app) =>
        app.MapDelete("/customers/{customerId:guid}", Handle)
            .ExcludeFromDescription();

    private static async Task<IResult> Handle(Guid customerId, TenantContext tenant, CatalogDbContext db, CancellationToken cancellationToken)
    {
        tenant.ActForUser(customerId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SavedPrograms.Where(s => s.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
