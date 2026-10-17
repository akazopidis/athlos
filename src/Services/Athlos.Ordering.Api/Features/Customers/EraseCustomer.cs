using Athlos.Ordering.Api.Data;
using Athlos.Ordering.Api.Features.Checkout;
using Athlos.Tenancy;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Ordering.Api.Features.Customers;

// Deleting a customer's account (design §5.2), called by tenancy-api: their orders stay, because the
// coaches need them for accounting, but the email address goes, and so do their cart checkouts. The
// customer id stays too: it is the Entra object id of an account that no longer exists, so it names
// nobody. Their open Stripe payment pages are closed, so the deleted account cannot be charged; one
// paid anyway (in the moment before) is refunded by the webhook, because its order has no address left.
//
// CROSS-TENANT SLICE (design §3.2, "a customer's own rows, for account deletion"): finding the
// tenants that hold the customer's orders ignores the tenant filter and relies on the customer filter
// below and on RLS (customer_id = app.user_id). The changes then run one tenant at a time, inside
// that tenant, like any write. Idempotent.
public static class EraseCustomer
{
    public const string ErasedEmail = "erased@deleted.invalid";

    public static void MapEraseCustomer(this IEndpointRouteBuilder app) =>
        app.MapDelete("/internal/customers/{customerId:guid}", Handle)
            .ExcludeFromDescription();

    private static async Task<IResult> Handle(
        Guid customerId, TenantContext tenant, OrderingDbContext db, IServiceScopeFactory scopes, ICheckoutSessions stripe,
        CancellationToken cancellationToken)
    {
        tenant.ActForUser(customerId);
        List<Guid> tenants;
        List<string> openSessions;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            tenants = await db.Orders.AsNoTracking()
                .IgnoreQueryFilters([TenantDbContext.TenantFilter])
                .Where(o => o.CustomerId == customerId && o.CustomerEmail != ErasedEmail)
                .Select(o => o.TenantId)
                .Distinct()
                .ToListAsync(cancellationToken);
            openSessions = await db.Orders.AsNoTracking()
                .IgnoreQueryFilters([TenantDbContext.TenantFilter])
                .Where(o => o.CustomerId == customerId && o.Status == OrderStatus.Pending && o.StripeCheckoutSessionId != null)
                .Select(o => o.StripeCheckoutSessionId!)
                .Distinct()
                .ToListAsync(cancellationToken);

            // The customer's own checkouts (RLS: customer_id = app.user_id) only link their orders.
            await db.Checkouts.Where(c => c.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        if (stripe.IsConfigured)
        {
            foreach (var session in openSessions)
            {
                await stripe.ExpireAsync(session, cancellationToken);
            }
        }

        foreach (var tenantId in tenants)
        {
            await scopes.InTenantAsync<OrderingDbContext>(tenantId, orders => orders.Orders
                .Where(o => o.CustomerId == customerId)
                .ExecuteUpdateAsync(o => o.SetProperty(x => x.CustomerEmail, ErasedEmail), cancellationToken), cancellationToken);
        }

        return TypedResults.NoContent();
    }
}
