using Athlos.Ordering.Api.Data;
using Athlos.Ordering.Api.Features.Checkout;
using Athlos.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Ordering.Api.Features.Refunds;

// The coach refunds one of their sales (design §2.2): the customer gets the price back, the coach's
// share is taken back, and the customer loses access to the program. Owners only; staff see sales but
// cannot give money back. Another tenant's order is not found (404), as everywhere.
// Not in the studio group's transaction: the Stripe calls must not hold one of the service's three
// database connections; OrderRefunds enters the tenant for each short transaction itself.
public static class RefundSale
{
    public static void MapRefundSale(this IEndpointRouteBuilder studio) =>
        studio.MapPost("/sales/{orderId:guid}/refund", Handle)
            .WithName("RefundSale")
            .WithTags("Sales");

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> Handle(
        string tenantSlug, Guid orderId, ITenantContext tenant, OrderingDbContext db, ICheckoutSessions stripe, IServiceScopeFactory scopes,
        CancellationToken cancellationToken)
    {
        // The sale must be this tenant's, under this address (read in the tenant, so RLS applies).
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            if (!await db.Orders.AnyAsync(o => o.Id == orderId && o.CoachSlug == tenantSlug && o.Status == OrderStatus.Paid, cancellationToken))
            {
                return TypedResults.NotFound();
            }
        }

        if (tenant.Role != TenantRole.Owner)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only the owner can refund a sale.");
        }

        if (!stripe.IsConfigured)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Payments are not configured for this environment.");
        }

        return await OrderRefunds.RefundAsync(tenant.TenantId!.Value, orderId, refundAtStripe: true, stripe, scopes, cancellationToken) switch
        {
            OrderRefunds.Outcome.Refunded => TypedResults.NoContent(),
            OrderRefunds.Outcome.BeforeTheCart => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "This sale was paid before the cart; refund it in the Stripe dashboard, which updates Athlos."),
            _ => TypedResults.NotFound(),
        };
    }
}
