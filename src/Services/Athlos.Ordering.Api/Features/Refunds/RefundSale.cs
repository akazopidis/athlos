using Athlos.Ordering.Api.Data;
using Athlos.Ordering.Api.Features.Checkout;
using Athlos.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Ordering.Api.Features.Refunds;

// The coach refunds one of their sales (design §2.2): the customer gets the price back, the coach's
// share is taken back, and the customer loses access to the program. Owners only; staff see sales but
// cannot give money back. Within the refund window only (14 days after payment by default). Another
// tenant's order is not found (404), as everywhere.
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
        IConfiguration configuration, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        // The sale must be this tenant's, under this address (read in the tenant, so RLS applies).
        DateTimeOffset? paidAt;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            paidAt = await db.Orders
                .Where(o => o.Id == orderId && o.CoachSlug == tenantSlug && o.Status == OrderStatus.Paid)
                .Select(o => o.PaidAt)
                .SingleOrDefaultAsync(cancellationToken);
            if (paidAt is null)
            {
                return TypedResults.NotFound();
            }
        }

        if (tenant.Role != TenantRole.Owner)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only the owner can refund a sale.");
        }

        var window = OrderRefunds.RefundWindow(configuration);
        if (paidAt.Value + window < DateTimeOffset.UtcNow)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
                title: $"Sales can be refunded for {window.TotalDays:0} days after payment; this one is older.");
        }

        if (!stripe.IsConfigured)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Payments are not configured for this environment.");
        }

        var logger = loggers.CreateLogger("Athlos.Ordering.Refunds");
        return await OrderRefunds.RefundAsync(tenant.TenantId!.Value, orderId, OrderRefunds.Cause.Coach, stripe, scopes, configuration, logger, cancellationToken) switch
        {
            OrderRefunds.Outcome.Refunded => TypedResults.NoContent(),
            OrderRefunds.Outcome.ShareNotRecovered => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Stripe could not take your share of this sale back from your Stripe account, so nothing was refunded.",
                detail: "Check your Stripe balance, then try again."),
            OrderRefunds.Outcome.BeforeTheCart => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "This sale was paid before the cart; refund it in the Stripe dashboard, which updates Athlos."),
            _ => TypedResults.NotFound(),
        };
    }
}
