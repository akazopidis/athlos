using Athlos.Ordering.Api.Data;
using Athlos.Ordering.Api.Features.Checkout;
using Athlos.Ordering.Api.Features.Refunds;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Ordering.Api.Features.Tenants;

// Called by tenancy-api before it erases a workspace (design §3.4): a deleted workspace's programs are
// gone, so nothing may still be sold, or still be owed back, once it is deleted.
// 1. Sales still inside the refund window (Payments:RefundWindowDays) refuse the deletion: their
//    customers could still ask for their money back, and only the coach can give it. 409, with the
//    date the last one runs out; the coach refunds them in the studio or waits.
// 2. Open Stripe payment pages for the workspace's programs are closed (expired), so nobody pays for
//    a program that is about to be erased. A cart's page holds other coaches' programs too; the
//    customer starts that checkout again. A page already paid at Stripe, whose payment event has not
//    arrived yet, refuses the deletion until it has (409).
// Read and changed inside the tenant, like every write. Idempotent: a second call finds nothing open.
public static class PrepareTenantDeletion
{
    public static void MapPrepareTenantDeletion(this IEndpointRouteBuilder app) =>
        app.MapPost("/internal/tenants/{tenantId:guid}/prepare-deletion", Handle)
            .ExcludeFromDescription();

    private static async Task<IResult> Handle(
        Guid tenantId, IServiceScopeFactory scopes, ICheckoutSessions stripe, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var refundableSince = DateTimeOffset.UtcNow - OrderRefunds.RefundWindow(configuration);
        DateTimeOffset? lastRefundable = null;
        List<string> openSessions = [];
        await scopes.InTenantAsync<OrderingDbContext>(tenantId, async db =>
        {
            lastRefundable = await db.Orders
                .Where(o => o.Status == OrderStatus.Paid && o.PaidAt > refundableSince)
                .MaxAsync(o => o.PaidAt, cancellationToken);
            openSessions = await db.Orders
                .Where(o => o.Status == OrderStatus.Pending && o.StripeCheckoutSessionId != null)
                .Select(o => o.StripeCheckoutSessionId!)
                .Distinct()
                .ToListAsync(cancellationToken);
        }, cancellationToken);

        if (lastRefundable is { } paidAt)
        {
            var until = paidAt + OrderRefunds.RefundWindow(configuration);
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
                title: $"Some sales can still be refunded, until {until.UtcDateTime:d MMMM yyyy}. Refund them in Sales first, or delete the workspace after that date.");
        }

        if (openSessions.Count > 0 && stripe.IsConfigured)
        {
            foreach (var session in openSessions)
            {
                if (!await stripe.ExpireAsync(session, cancellationToken))
                {
                    return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
                        title: "A customer has just paid for one of your programs. Try again in a few minutes.");
                }
            }
        }

        return TypedResults.NoContent();
    }
}
