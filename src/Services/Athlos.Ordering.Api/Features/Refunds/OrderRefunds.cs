using Athlos.Messaging;
using Athlos.Messaging.Contracts;
using Athlos.Ordering.Api.Data;
using Athlos.Ordering.Api.Features.Checkout;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Ordering.Api.Features.Refunds;

// Refunding one paid order (design §2.2), the same way whoever starts it: the coach in the studio, a
// full refund made at Stripe, or a lost dispute.
// 1. The customer's money: given back here (RefundAtStripe) or already given back by Stripe.
// 2. The coach's share: the transfer is reversed, or a waiting PayoutRetry is dropped.
// 3. Inside the order's tenant, in one transaction: Paid -> Refunded (only once), and OrderRefunded
//    to the outbox, so enrollment-api removes the access.
// Every step is idempotent per order, so a retry completes a refund that stopped halfway.
public static class OrderRefunds
{
    // How long after payment a coach may refund a sale (Payments:RefundWindowDays, default 14: the EU's
    // withdrawal period for online purchases). Refunds made at Stripe and lost disputes have no window:
    // the money has already gone back, and Athlos follows.
    public static TimeSpan RefundWindow(IConfiguration configuration) =>
        TimeSpan.FromDays(configuration.GetValue("Payments:RefundWindowDays", 14));

    public enum Outcome
    {
        Refunded,
        NotFound,
        // Paid before the cart, as a destination charge: refund it at Stripe, with its transfer.
        BeforeTheCart,
    }

    public static async Task<Outcome> RefundAsync(
        Guid tenantId, Guid orderId, bool refundAtStripe, ICheckoutSessions stripe, IServiceScopeFactory scopes, CancellationToken cancellationToken)
    {
        Order? order = null;
        await scopes.InTenantAsync<OrderingDbContext>(tenantId, async db => order = await db.Orders.AsNoTracking()
            .SingleOrDefaultAsync(o => o.Id == orderId && o.Status == OrderStatus.Paid, cancellationToken), cancellationToken);
        if (order is null)
        {
            return Outcome.NotFound;
        }

        var beforeTheCart = order.CheckoutId is null;
        if (refundAtStripe)
        {
            if (beforeTheCart)
            {
                return Outcome.BeforeTheCart;
            }

            var paymentIntentId = order.StripePaymentIntentId
                ?? await stripe.PaymentIntentOfSessionAsync(order.StripeCheckoutSessionId!, cancellationToken)
                ?? throw new InvalidOperationException($"Order {order.Id} is paid but has no payment.");
            await stripe.RefundAsync(order, paymentIntentId, "requested_by_customer", cancellationToken);
        }

        if (!beforeTheCart && order.StripeTransferId is not null)
        {
            await stripe.ReverseTransferAsync(order, cancellationToken);
        }

        await scopes.InTenantAsync<OrderingDbContext>(tenantId, async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var changed = await db.Orders
                .Where(o => o.Id == orderId && o.Status == OrderStatus.Paid)
                .ExecuteUpdateAsync(o => o
                    .SetProperty(x => x.Status, OrderStatus.Refunded)
                    .SetProperty(x => x.RefundedAt, now)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken);
            if (changed == 0)
            {
                return;
            }

            // A share never transferred is not owed any more.
            await db.PayoutRetries.Where(r => r.OrderId == orderId).ExecuteDeleteAsync(cancellationToken);
            db.OutboxMessages.Add(OutboxMessage.For(OrderRefunded.Queue,
                new OrderRefunded(order.Id, order.TenantId, order.CustomerId, order.ProgramId, now)));
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

        return Outcome.Refunded;
    }

    // A full refund or a lost dispute at Stripe: every order the payment paid for. The orders come from
    // the payment's metadata, each looked up inside the tenant named next to it.
    public static async Task<bool> RefundPaymentAsync(
        string paymentIntentId, ICheckoutSessions stripe, IServiceScopeFactory scopes, CancellationToken cancellationToken)
    {
        var (orders, _) = await stripe.OrdersOfPaymentAsync(paymentIntentId, cancellationToken);
        foreach (var (tenantId, orderId) in orders)
        {
            await RefundAsync(tenantId, orderId, refundAtStripe: false, stripe, scopes, cancellationToken);
        }

        return true;
    }
}
