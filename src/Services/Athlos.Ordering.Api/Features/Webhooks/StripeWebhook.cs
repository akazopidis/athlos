using Athlos.Messaging;
using Athlos.Messaging.Contracts;
using Athlos.Ordering.Api.Data;
using Athlos.Ordering.Api.Features.Checkout;
using Athlos.Ordering.Api.Features.Customers;
using Athlos.Ordering.Api.Features.Refunds;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Stripe;
using Stripe.Checkout;

namespace Athlos.Ordering.Api.Features.Webhooks;

// Stripe calls POST /webhooks/stripe through the gateway (design §2.2, §5.1). It verifies the
// Stripe-Signature and rejects events older than five minutes, then handles:
//
// Checkout sessions (paid, failed, expired). The session's metadata names its orders, one (tenant,
// order) pair per program, possibly of several coaches. For each order, inside its own tenant, so RLS
// applies:
// 1. send the coach's share as a Stripe Transfer (cart sessions); a failed transfer is queued for
//    PayoutRetries and does not hold up the order, and the coach is emailed to fix their account;
// 2. in one transaction, mark the order Paid only if it is still Pending, and write OrderPaid and the
//    coach's "you made a sale" email to the outbox.
// 3. once the orders are Paid, one purchase confirmation to the customer, listing every program the
//    checkout paid for (one email for a cart, not one per program). It is written after the orders'
//    transactions: a crash in between loses this email, never an order or its access.
// A program the customer already owns (two pages paid) is refunded instead, and so is an order of a
// customer who deleted their account before the payment arrived.
//
// Refunds and disputes. A dispute opened (charge.dispute.created) emails the coaches of the payment;
// nothing else changes until it closes. A full refund made at Stripe (charge.refunded) or a lost dispute
// (charge.dispute.closed) refunds every order of that payment, through OrderRefunds: the coach's
// transfer is reversed and enrollment removes the access. A partial refund refunds the orders its
// refunds name (athlos_order_id): Athlos' own, or one made in the dashboard with that metadata; one
// without it is logged for a person to settle. A refund for a payment whose orders are still Pending
// (its checkout event failed and Stripe is retrying it) answers 500, so Stripe sends it again after the
// payment is recorded; otherwise the late payment event would hand out access and the coach's share
// for money already returned.
//
// The event id is recorded last, so a retried event returns at once. Every step is idempotent (per
// order at Stripe, conditional updates here), so an event that fails halfway is completed by Stripe's
// retry.
public static partial class StripeWebhook
{
    private const long MaxPayloadBytes = 64 * 1024;

    public static void MapStripeWebhook(this IEndpointRouteBuilder app) =>
        app.MapPost("/webhooks/stripe", Handle)
            .ExcludeFromDescription();

    // To the workspace owners (design §5.3); the worker looks up their addresses with tenancy-api.
    // The customer's address is not in it: the coach sees buyers in the studio's sales list.
    public static EmailNotification SaleEmail(Order order, IConfiguration configuration)
    {
        var publicUrl = new Uri(configuration["App:PublicUrl"] ?? throw new InvalidOperationException("App:PublicUrl is not configured."));
        return new EmailNotification(EmailTemplates.SaleMade, To: [], ToOwnersOfTenant: order.TenantId, Locale: "en", new Dictionary<string, string>
        {
            ["programTitle"] = order.ProgramTitle,
            ["price"] = MoneyText.Format(order.Amount, order.Currency),
            ["earnings"] = MoneyText.Format(order.Amount - order.PlatformFee, order.Currency),
            ["salesUrl"] = new Uri(publicUrl, $"/studio/{order.CoachSlug}").ToString(),
        });
    }

    private static async Task<Results<Ok, BadRequest, ProblemHttpResult>> Handle(
        HttpRequest request, StripeWebhookVerifier verifier, OrderingDbContext db, ICheckoutSessions checkout, IServiceScopeFactory scopes,
        IConfiguration configuration, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (!verifier.IsConfigured)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Stripe webhooks are not configured.");
        }

        if (request.ContentLength > MaxPayloadBytes)
        {
            return TypedResults.BadRequest();
        }

        using var reader = new StreamReader(request.Body);
        var payload = await reader.ReadToEndAsync(cancellationToken);
        var stripeEvent = verifier.Verify(payload, request.Headers["Stripe-Signature"]);
        if (stripeEvent is null)
        {
            return TypedResults.BadRequest();
        }

        if (await db.ProcessedStripeEvents.AnyAsync(e => e.StripeEventId == stripeEvent.Id, cancellationToken))
        {
            return TypedResults.Ok(); // Already handled: Stripe retried.
        }

        var logger = loggers.CreateLogger("Athlos.Ordering.Webhooks");
        var handled = stripeEvent.Data.Object switch
        {
            Session session => await HandleSessionAsync(stripeEvent.Type, session, checkout, scopes, configuration, logger, cancellationToken),
            Charge { Refunded: true, PaymentIntentId: { } refundedPayment } =>
                await OrderRefunds.RefundPaymentAsync(refundedPayment, OrderRefunds.Cause.AtStripe, checkout, scopes, configuration, logger, cancellationToken),
            Charge { AmountRefunded: > 0, PaymentIntentId: { } partlyRefunded } when stripeEvent.Type == "charge.refunded" =>
                await OrderRefunds.RefundTaggedOrdersAsync(partlyRefunded, checkout, scopes, configuration, logger, cancellationToken),
            Dispute { PaymentIntentId: { } openedOn } when stripeEvent.Type == "charge.dispute.created" =>
                await OrderRefunds.NotifyDisputeAsync(openedOn, EmailTemplates.SaleDisputed, checkout, scopes, configuration, cancellationToken),
            Dispute { Status: "won", PaymentIntentId: { } wonOn } when stripeEvent.Type == "charge.dispute.closed" =>
                await OrderRefunds.NotifyDisputeAsync(wonOn, EmailTemplates.DisputeWon, checkout, scopes, configuration, cancellationToken),
            Dispute { Status: "lost", PaymentIntentId: { } disputedPayment } when stripeEvent.Type == "charge.dispute.closed" =>
                await OrderRefunds.RefundPaymentAsync(disputedPayment, OrderRefunds.Cause.LostDispute, checkout, scopes, configuration, logger, cancellationToken),
            // Other events are acknowledged and ignored.
            _ => true,
        };

        // An order not handled yet (a payment intent missing on a paid session): Stripe retries.
        if (!handled)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "The event could not be handled yet.");
        }

        db.ProcessedStripeEvents.Add(new ProcessedStripeEvent { StripeEventId = stripeEvent.Id });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent delivery of the same event finished first; each order was still handled once.
        }

        return TypedResults.Ok();
    }

    // To the workspace owners: their share waits until their Stripe account can receive it. The studio's
    // overview reads the account's status from Stripe and offers to continue the setup.
    public static EmailNotification PayoutFailedEmail(Order order, IConfiguration configuration)
    {
        var publicUrl = new Uri(configuration["App:PublicUrl"] ?? throw new InvalidOperationException("App:PublicUrl is not configured."));
        return new EmailNotification(EmailTemplates.PayoutFailed, To: [], ToOwnersOfTenant: order.TenantId, Locale: "en", new Dictionary<string, string>
        {
            ["programTitle"] = order.ProgramTitle,
            ["price"] = MoneyText.Format(order.Amount, order.Currency),
            ["earnings"] = MoneyText.Format(order.Amount - order.PlatformFee, order.Currency),
            ["studioUrl"] = new Uri(publicUrl, $"/studio/{order.CoachSlug}").ToString(),
        });
    }

    // To the customer, once per checkout: the programs it paid for, the total and the refund promise.
    public static EmailNotification PurchaseEmail(IReadOnlyList<Order> orders, IConfiguration configuration)
    {
        var publicUrl = new Uri(configuration["App:PublicUrl"] ?? throw new InvalidOperationException("App:PublicUrl is not configured."));
        var first = orders[0];
        var summary = orders.Count == 1 ? first.ProgramTitle : $"{first.ProgramTitle} and {orders.Count - 1} more";
        return new EmailNotification(EmailTemplates.PurchaseConfirmation, To: [first.CustomerEmail], ToOwnersOfTenant: null, Locale: "en",
            new Dictionary<string, string>
            {
                ["summary"] = summary,
                ["items"] = string.Join('\n', orders.Select(o => $"• {o.ProgramTitle} by {o.CoachSlug}, {MoneyText.Format(o.Amount, o.Currency)}"
                    + (o.DiscountCode is null ? "" : $" (code {o.DiscountCode}, {MoneyText.Format(o.DiscountAmount, o.Currency)} off)"))),
                ["total"] = MoneyText.Format(orders.Sum(o => o.Amount), first.Currency),
                ["refundDays"] = OrderRefunds.RefundWindow(configuration).TotalDays.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                ["programsUrl"] = new Uri(publicUrl, "/me").ToString(),
                ["receiptUrl"] = new Uri(publicUrl, $"/me/orders/{first.CheckoutId}/receipt").ToString(),
            });
    }

    private static async Task<bool> HandleSessionAsync(
        string eventType, Session session, ICheckoutSessions checkout, IServiceScopeFactory scopes, IConfiguration configuration, ILogger logger,
        CancellationToken cancellationToken)
    {
        var outcome = eventType switch
        {
            "checkout.session.completed" or "checkout.session.async_payment_succeeded" => OrderStatus.Paid,
            "checkout.session.expired" or "checkout.session.async_payment_failed" => OrderStatus.Failed,
            _ => (OrderStatus?)null,
        };

        var (orders, legacy) = CheckoutMetadata.Read(session.Metadata);

        // A completed session may still be waiting for a delayed payment method.
        if (outcome is null || orders.Count == 0 || (outcome == OrderStatus.Paid && session.PaymentStatus != "paid"))
        {
            return true;
        }

        var paymentIntentId = session.PaymentIntentId;
        if (outcome == OrderStatus.Paid && !legacy && paymentIntentId is null)
        {
            return false;
        }

        var paid = new List<Order>();
        foreach (var (tenantId, orderId) in orders)
        {
            var (order, alreadyOwned) = await FindPendingAsync(scopes, tenantId, orderId, session.Id, cancellationToken);
            if (order is null)
            {
                continue;
            }

            // The customer already owns this program (two checkout pages paid before either webhook
            // arrived), or deleted their account meanwhile: the amount goes back and the coach gets
            // nothing for this order.
            var erased = order.CustomerEmail == EraseCustomer.ErasedEmail;
            if (outcome == OrderStatus.Paid && (alreadyOwned || erased) && !legacy)
            {
                await checkout.RefundAsync(order, paymentIntentId!, alreadyOwned ? "duplicate" : "requested_by_customer", cancellationToken);
                await CompleteAsync(scopes, tenantId, orderId, session.Id, new Completion(OrderStatus.Refunded, paymentIntentId), configuration, cancellationToken);
                continue;
            }

            // Before the cart, destination charges paid the coach directly; there is nothing to transfer.
            string? transferId = null;
            string? transferError = null;
            if (outcome == OrderStatus.Paid && !legacy)
            {
                try
                {
                    transferId = await checkout.TransferAsync(order, paymentIntentId!, cancellationToken);
                }
                catch (StripeException e)
                {
                    // The customer has paid, so the order is still Paid and the rest of the cart goes
                    // on; the coach's share is queued for PayoutRetries.
                    transferError = e.StripeError?.Code ?? "stripe_error";
                    LogTransferFailed(logger, order.Id, tenantId, transferError);
                }
            }

            var completion = new Completion(outcome.Value, outcome == OrderStatus.Paid ? paymentIntentId : null, transferId, transferError);
            if (await CompleteAsync(scopes, tenantId, orderId, session.Id, completion, configuration, cancellationToken) is { } completed)
            {
                paid.Add(completed);
            }
        }

        // Counted once each order is committed as Paid; a retried event never reaches this point.
        foreach (var order in paid)
        {
            OrderMetrics.OrderPaid(order.TenantId, order.CoachSlug, order.Currency);
        }

        if (paid.Count > 0)
        {
            await scopes.InTenantAsync<OrderingDbContext>(paid[0].TenantId, async db =>
            {
                db.OutboxMessages.Add(OutboxMessage.For(EmailNotification.Queue, PurchaseEmail(paid, configuration)));
                await db.SaveChangesAsync(cancellationToken);
            }, cancellationToken);
        }

        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Transfer of order {OrderId} (tenant {TenantId}) to the coach failed: {StripeCode}; queued for a retry")]
    private static partial void LogTransferFailed(ILogger logger, Guid orderId, Guid tenantId, string? stripeCode);

    // The order, read inside the tenant the metadata names: another tenant's order is not there. Also
    // whether the customer already has a paid order for the same program (with the same coach).
    private static async Task<(Order? Order, bool AlreadyOwned)> FindPendingAsync(
        IServiceScopeFactory scopes, Guid tenantId, Guid orderId, string sessionId, CancellationToken cancellationToken)
    {
        Order? order = null;
        var owned = false;
        await scopes.InTenantAsync<OrderingDbContext>(tenantId, async db =>
        {
            order = await db.Orders.AsNoTracking()
                .SingleOrDefaultAsync(o => o.Id == orderId && o.StripeCheckoutSessionId == sessionId && o.Status == OrderStatus.Pending, cancellationToken);
            owned = order is not null && await db.Orders.AnyAsync(
                o => o.CustomerId == order.CustomerId && o.ProgramId == order.ProgramId && o.Status == OrderStatus.Paid && o.Id != order.Id, cancellationToken);
        }, cancellationToken);
        return (order, owned);
    }

    private sealed record Completion(OrderStatus Outcome, string? PaymentIntentId, string? TransferId = null, string? TransferError = null);

    // Moves the order out of Pending once: the conditional update locks the row, so of two concurrent
    // deliveries only one sees it change. Returns the order if it was marked Paid.
    private static async Task<Order?> CompleteAsync(
        IServiceScopeFactory scopes, Guid tenantId, Guid orderId, string sessionId, Completion completion, IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        Order? paid = null;
        await scopes.InTenantAsync<OrderingDbContext>(tenantId, async db =>
        {
            var now = DateTimeOffset.UtcNow;
            DateTimeOffset? paidAt = completion.Outcome is OrderStatus.Paid or OrderStatus.Refunded ? now : null;
            DateTimeOffset? refundedAt = completion.Outcome == OrderStatus.Refunded ? now : null;
            var changed = await db.Orders
                .Where(o => o.Id == orderId && o.StripeCheckoutSessionId == sessionId && o.Status == OrderStatus.Pending)
                .ExecuteUpdateAsync(o => o
                    .SetProperty(x => x.Status, completion.Outcome)
                    .SetProperty(x => x.PaidAt, paidAt)
                    .SetProperty(x => x.RefundedAt, refundedAt)
                    .SetProperty(x => x.StripePaymentIntentId, completion.PaymentIntentId)
                    .SetProperty(x => x.StripeTransferId, completion.TransferId)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken);
            if (changed == 0 || completion.Outcome != OrderStatus.Paid)
            {
                return;
            }

            var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, cancellationToken);
            db.OutboxMessages.Add(OutboxMessage.For(OrderPaid.Queue, new OrderPaid(
                order.Id, order.TenantId, order.CustomerId, order.CustomerEmail, order.ProgramId, order.ProgramTitle, order.CoachSlug,
                order.Amount, order.Currency, order.PaidAt!.Value)));
            db.OutboxMessages.Add(OutboxMessage.For(EmailNotification.Queue, SaleEmail(order, configuration)));
            if (completion.TransferError is { } error)
            {
                db.OutboxMessages.Add(OutboxMessage.For(EmailNotification.Queue, PayoutFailedEmail(order, configuration)));
                db.PayoutRetries.Add(new PayoutRetry
                {
                    OrderId = order.Id,
                    TenantId = order.TenantId,
                    CoachSlug = order.CoachSlug,
                    Amount = order.Amount - order.PlatformFee,
                    Currency = order.Currency,
                    Attempts = 1,
                    NextAttemptAt = now + Payouts.PayoutRetries.Backoff(1),
                    LastError = error,
                });
            }

            await db.SaveChangesAsync(cancellationToken);
            paid = order;
        }, cancellationToken);
        return paid;
    }
}
