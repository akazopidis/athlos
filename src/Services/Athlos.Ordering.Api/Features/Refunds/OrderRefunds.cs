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
//    they deleted their account. A refund the coach did not make (at Stripe, or a lost dispute) also
//    emails the coach's owners, saying why.
// Every step is idempotent per order, at Stripe too (an existing refund or reversal is found by its
// order, not by a 24-hour idempotency key), so a retry completes a refund that stopped halfway.
public static partial class OrderRefunds
{
    // How long after payment a coach may refund a sale (Payments:RefundWindowDays, default 14: the EU's
    // withdrawal period for online purchases). Refunds made at Stripe and lost disputes have no window:
    // the money has already gone back, and Athlos follows.
    public static TimeSpan RefundWindow(IConfiguration configuration) =>
        TimeSpan.FromDays(configuration.GetValue("Payments:RefundWindowDays", 14));

    // Who or what refunded the order.
    public enum Cause
    {
        // The owner, in the studio: Athlos refunds at Stripe.
        Coach,
        // The platform, in the Stripe dashboard: the money has gone back already.
        AtStripe,
        // The customer's bank decided a dispute against the platform: the money has gone back already.
        LostDispute,
    }

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
        Guid tenantId, Guid orderId, Cause cause, ICheckoutSessions stripe, IServiceScopeFactory scopes, IConfiguration configuration,
        ILogger logger, CancellationToken cancellationToken)
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
        if (cause == Cause.Coach)
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

            if (cause != Cause.Coach)
            {
                db.OutboxMessages.Add(OutboxMessage.For(EmailNotification.Queue, CoachEmail(EmailTemplates.SaleRefunded, order, configuration,
                    cause == Cause.LostDispute ? "the customer's bank decided a payment dispute in their favour" : "the platform refunded it in Stripe")));
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

    // To the workspace's owners (the worker looks up their addresses, design §5.3): a refund or dispute
    // of one of their sales, with what happens to their share.
    public static EmailNotification CoachEmail(string template, Order order, IConfiguration configuration, string? reason = null)
    {
        var publicUrl = new Uri(configuration["App:PublicUrl"] ?? throw new InvalidOperationException("App:PublicUrl is not configured."));
        var data = new Dictionary<string, string>
        {
            ["programTitle"] = order.ProgramTitle,
            ["price"] = MoneyText.Format(order.Amount, order.Currency),
            ["earnings"] = MoneyText.Format(order.Amount - order.PlatformFee, order.Currency),
            ["salesUrl"] = new Uri(publicUrl, $"/studio/{order.CoachSlug}/sales").ToString(),
        };
        if (reason is not null)
        {
            data["reason"] = reason;
        }

        return new EmailNotification(template, To: [], ToOwnersOfTenant: order.TenantId, Locale: "en", data);
    }

    // A full refund or a lost dispute at Stripe: every order the payment paid for. The orders come from
    // the payment's metadata, each looked up inside the tenant named next to it. False while an order is
    // still Pending, so the webhook answers 500 and Stripe sends the event again later.
    public static async Task<bool> RefundPaymentAsync(
        string paymentIntentId, Cause cause, ICheckoutSessions stripe, IServiceScopeFactory scopes, IConfiguration configuration, ILogger logger,
        CancellationToken cancellationToken)
    {
        var (orders, _) = await stripe.OrdersOfPaymentAsync(paymentIntentId, cancellationToken);
        var done = true;
        foreach (var (tenantId, orderId) in orders)
        {
            done &= await RefundAsync(tenantId, orderId, cause, stripe, scopes, configuration, logger, cancellationToken) != Outcome.NotPaidYet;
        }

        return done;
    }

    // A partial refund at Stripe. Athlos' own refunds carry their order (athlos_order_id) and are recorded
    // already, or are completed here. A refund made in the Stripe dashboard refunds one program in Athlos
    // when it is given that metadata too; without it, Athlos cannot tell which program it was for, and it
    // is logged for a person to settle.
    public static async Task<bool> RefundTaggedOrdersAsync(
        string paymentIntentId, ICheckoutSessions stripe, IServiceScopeFactory scopes, IConfiguration configuration, ILogger logger,
        CancellationToken cancellationToken)
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
            done &= await RefundAsync(tenantId, orderId, Cause.AtStripe, stripe, scopes, configuration, logger, cancellationToken) != Outcome.NotPaidYet;
        }

        return done;
    }

    // A customer disputed a payment (charge.dispute.created, template sale-disputed), or the platform won
    // the dispute (charge.dispute.closed as won, dispute-won): every coach the payment paid is told, once per
    // event (the webhook records the event id). Nothing else changes: a lost dispute refunds the orders
    // (RefundPaymentAsync), a won one leaves the sale as it is. An order still Pending waits, like a refund.
    public static async Task<bool> NotifyDisputeAsync(
        string paymentIntentId, string template, ICheckoutSessions stripe, IServiceScopeFactory scopes, IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var (orders, _) = await stripe.OrdersOfPaymentAsync(paymentIntentId, cancellationToken);
        var done = true;
        foreach (var (tenantId, orderId) in orders)
        {
            await scopes.InTenantAsync<OrderingDbContext>(tenantId, async db =>
            {
                var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == orderId, cancellationToken);
                if (order?.Status == OrderStatus.Pending)
                {
                    done = false;
                }
                else if (order?.Status == OrderStatus.Paid)
                {
                    db.OutboxMessages.Add(OutboxMessage.For(EmailNotification.Queue, CoachEmail(template, order, configuration)));
                    await db.SaveChangesAsync(cancellationToken);
                }
            }, cancellationToken);
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
