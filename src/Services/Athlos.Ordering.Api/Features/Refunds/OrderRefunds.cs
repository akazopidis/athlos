using Athlos.Messaging;
using Athlos.Messaging.Contracts;
using Athlos.Ordering.Api.Data;
using Athlos.Ordering.Api.Features.Checkout;
using Athlos.Ordering.Api.Features.Customers;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;
using Stripe;

namespace Athlos.Ordering.Api.Features.Refunds;

// Refunding one paid order (design §2.2), the same way whoever starts it: the coach in the studio, a
// refund made at Stripe, or a lost dispute.
// 1. The money, at Stripe.
//    - A coach's refund takes the coach's share back first (the transfer is reversed), then gives the
//      customer the price back. If Stripe refuses the reversal, nothing has changed and the coach can
//      try again; the platform never pays a refund the coach's share does not cover.
//    - A refund made at Stripe, or a lost dispute, has already given the money back; the reversal
//      follows, and if Stripe refuses it the order is refunded in Athlos anyway (the error is logged
//      for a person to settle with the coach).
//    - A share still waiting in PayoutRetries was never sent; the retry is dropped.
// 2. Inside the order's tenant, in one transaction: Paid -> Refunded (only once), OrderRefunded to the
//    outbox, so enrollment-api removes the access, and the customer's refund email (design §5.3), unless
//    they deleted their account.
// Every step is idempotent per order, at Stripe too (an existing refund or reversal is found by its
// order, not by a 24-hour idempotency key), so a retry completes a refund that stopped halfway.
public static partial class OrderRefunds
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
        // Still Pending: Stripe's refund or dispute event arrived before the payment's own event (which
        // failed and is being retried). The webhook asks Stripe to send the refund again later.
        NotPaidYet,
        // A coach's refund: Stripe would not take the coach's share back. Nothing changed.
        ShareNotRecovered,
    }

    public static async Task<Outcome> RefundAsync(
        Guid tenantId, Guid orderId, bool refundAtStripe, ICheckoutSessions stripe, IServiceScopeFactory scopes, ILogger logger,
        CancellationToken cancellationToken)
    {
        Order? order = null;
        await scopes.InTenantAsync<OrderingDbContext>(tenantId, async db => order = await db.Orders.AsNoTracking()
            .SingleOrDefaultAsync(o => o.Id == orderId, cancellationToken), cancellationToken);
        if (order?.Status == OrderStatus.Pending)
        {
            return Outcome.NotPaidYet;
        }

        if (order?.Status != OrderStatus.Paid)
        {
            return Outcome.NotFound;
        }

        var beforeTheCart = order.CheckoutId is null;
        var transferred = !beforeTheCart && order.StripeTransferId is not null;
        if (refundAtStripe)
        {
            if (beforeTheCart)
            {
                return Outcome.BeforeTheCart;
            }

            if (transferred)
            {
                try
                {
                    await stripe.ReverseTransferAsync(order, cancellationToken);
                }
                catch (StripeException e)
                {
                    LogShareNotRecovered(logger, order.Id, tenantId, e.StripeError?.Code);
                    return Outcome.ShareNotRecovered;
                }
            }

            var paymentIntentId = order.StripePaymentIntentId
                ?? await stripe.PaymentIntentOfSessionAsync(order.StripeCheckoutSessionId!, cancellationToken)
                ?? throw new InvalidOperationException($"Order {order.Id} is paid but has no payment.");
            await stripe.RefundAsync(order, paymentIntentId, "requested_by_customer", cancellationToken);
        }
        else if (transferred)
        {
            try
            {
                await stripe.ReverseTransferAsync(order, cancellationToken);
            }
            catch (StripeException e)
            {
                // The customer has their money back already; their access goes regardless.
                LogReversalFailed(logger, order.Id, tenantId, e.StripeError?.Code);
            }
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
            if (order.CustomerEmail != EraseCustomer.ErasedEmail)
            {
                db.OutboxMessages.Add(OutboxMessage.For(EmailNotification.Queue, RefundEmail(order)));
            }

            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

        return Outcome.Refunded;
    }

    public static EmailNotification RefundEmail(Order order) =>
        new(EmailTemplates.RefundConfirmation, To: [order.CustomerEmail], ToOwnersOfTenant: null, Locale: "en",
            new Dictionary<string, string>
            {
                ["programTitle"] = order.ProgramTitle,
                ["coachSlug"] = order.CoachSlug,
                ["price"] = MoneyText.Format(order.Amount, order.Currency),
            });

    // A full refund or a lost dispute at Stripe: every order the payment paid for. The orders come from
    // the payment's metadata, each looked up inside the tenant named next to it. False while an order is
    // still Pending, so the webhook answers 500 and Stripe sends the event again later.
    public static async Task<bool> RefundPaymentAsync(
        string paymentIntentId, ICheckoutSessions stripe, IServiceScopeFactory scopes, ILogger logger, CancellationToken cancellationToken)
    {
        var (orders, _) = await stripe.OrdersOfPaymentAsync(paymentIntentId, cancellationToken);
        var done = true;
        foreach (var (tenantId, orderId) in orders)
        {
            done &= await RefundAsync(tenantId, orderId, refundAtStripe: false, stripe, scopes, logger, cancellationToken) != Outcome.NotPaidYet;
        }

        return done;
    }

    // A partial refund at Stripe. Athlos' own refunds carry their order (athlos_order_id) and are recorded
    // already, or are completed here. A refund made in the Stripe dashboard refunds one program in Athlos
    // when it is given that metadata too; without it, Athlos cannot tell which program it was for, and it
    // is logged for a person to settle.
    public static async Task<bool> RefundTaggedOrdersAsync(
        string paymentIntentId, ICheckoutSessions stripe, IServiceScopeFactory scopes, ILogger logger, CancellationToken cancellationToken)
    {
        var (orders, _) = await stripe.OrdersOfPaymentAsync(paymentIntentId, cancellationToken);
        var refunds = await stripe.RefundsOfPaymentAsync(paymentIntentId, cancellationToken);
        if (refunds.Any(order => order is null || !orders.Any(o => o.OrderId == order)))
        {
            LogUntaggedRefund(logger, paymentIntentId);
        }

        var done = true;
        foreach (var (tenantId, orderId) in orders.Where(o => refunds.Contains(o.OrderId)))
        {
            done &= await RefundAsync(tenantId, orderId, refundAtStripe: false, stripe, scopes, logger, cancellationToken) != Outcome.NotPaidYet;
        }

        return done;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refund of order {OrderId} (tenant {TenantId}) stopped: Stripe would not take the coach's share back ({StripeCode})")]
    private static partial void LogShareNotRecovered(ILogger logger, Guid orderId, Guid tenantId, string? stripeCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Order {OrderId} (tenant {TenantId}) was refunded at Stripe, but the coach's share could not be taken back ({StripeCode}); settle it by hand")]
    private static partial void LogReversalFailed(ILogger logger, Guid orderId, Guid tenantId, string? stripeCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Payment {PaymentIntentId} has a refund without an Athlos order; it needs settling by hand")]
    private static partial void LogUntaggedRefund(ILogger logger, string paymentIntentId);
}
