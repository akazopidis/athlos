using Athlos.Messaging;
using Athlos.Messaging.Contracts;
using Athlos.Ordering.Api.Data;
using Athlos.Ordering.Api.Features.Checkout;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Stripe;
using Stripe.Checkout;

namespace Athlos.Ordering.Api.Features.Webhooks;

// Stripe calls POST /webhooks/stripe through the gateway (design §2.2, §5.1):
// 1. verify the Stripe-Signature and reject events older than five minutes;
// 2. read the session's orders from its metadata (written by checkout): one (tenant, order) pair per
//    program, possibly of several coaches;
// 3. for each order, inside its own tenant, so RLS applies: send the coach's share as a Stripe
//    Transfer (paid sessions from the cart), then in one transaction mark the order Paid only if it
//    is still Pending, and write OrderPaid and the coach's "you made a sale" email to the outbox;
// 4. record the event id, so a retried event returns at once.
// Every step is idempotent (the transfer per order at Stripe, the conditional update here), so an
// event that fails halfway is safely completed by Stripe's retry.
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

        var outcome = stripeEvent.Type switch
        {
            "checkout.session.completed" or "checkout.session.async_payment_succeeded" => OrderStatus.Paid,
            "checkout.session.expired" or "checkout.session.async_payment_failed" => OrderStatus.Failed,
            _ => (OrderStatus?)null,
        };

        // Other event types are acknowledged and ignored.
        if (outcome is null || stripeEvent.Data.Object is not Session session)
        {
            return TypedResults.Ok();
        }

        var (orders, legacy) = CheckoutMetadata.Read(session.Metadata);
        if (orders.Count == 0)
        {
            return TypedResults.Ok();
        }

        // A completed session may still be waiting for a delayed payment method.
        if (outcome == OrderStatus.Paid && session.PaymentStatus != "paid")
        {
            return TypedResults.Ok();
        }

        if (await db.ProcessedStripeEvents.AnyAsync(e => e.StripeEventId == stripeEvent.Id, cancellationToken))
        {
            return TypedResults.Ok(); // Already handled: Stripe retried.
        }

        var paid = new List<Order>();
        foreach (var (tenantId, orderId) in orders)
        {
            var order = await FindPendingAsync(scopes, tenantId, orderId, session.Id, cancellationToken);
            if (order is null)
            {
                continue;
            }

            // Before the cart, destination charges paid the coach directly; there is nothing to transfer.
            string? transferId = null;
            if (outcome == OrderStatus.Paid && !legacy)
            {
                if (session.PaymentIntentId is not { } paymentIntentId || order.SellerAccountId is null)
                {
                    return TypedResults.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "The payment cannot be transferred.");
                }

                try
                {
                    transferId = await checkout.TransferAsync(order, paymentIntentId, cancellationToken);
                }
                catch (StripeException e)
                {
                    // The customer has paid, so the order is still Paid and the rest of the cart goes
                    // on; the coach's share waits, found by the Paid order without a transfer id.
                    LogTransferFailed(loggers.CreateLogger("Athlos.Ordering.Transfers"), order.Id, tenantId, e.StripeError?.Code);
                }
            }

            if (await CompleteAsync(scopes, tenantId, orderId, session.Id, outcome.Value, transferId, configuration, cancellationToken) is { } completed)
            {
                paid.Add(completed);
            }
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

        // Counted once each order is committed as Paid; a retried event never reaches this point.
        foreach (var order in paid)
        {
            OrderMetrics.OrderPaid(order.TenantId, order.CoachSlug, order.Currency);
        }

        return TypedResults.Ok();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Transfer of order {OrderId} (tenant {TenantId}) to the coach failed: {StripeCode}")]
    private static partial void LogTransferFailed(ILogger logger, Guid orderId, Guid tenantId, string? stripeCode);

    // The order, read inside the tenant the metadata names: another tenant's order is not there.
    private static async Task<Order?> FindPendingAsync(
        IServiceScopeFactory scopes, Guid tenantId, Guid orderId, string sessionId, CancellationToken cancellationToken)
    {
        Order? order = null;
        await scopes.InTenantAsync<OrderingDbContext>(tenantId, async db => order = await db.Orders.AsNoTracking()
            .SingleOrDefaultAsync(o => o.Id == orderId && o.StripeCheckoutSessionId == sessionId && o.Status == OrderStatus.Pending, cancellationToken),
            cancellationToken);
        return order;
    }

    // Moves the order out of Pending once: the conditional update locks the row, so of two concurrent
    // deliveries only one sees it change. Returns the order if it was marked Paid.
    private static async Task<Order?> CompleteAsync(
        IServiceScopeFactory scopes, Guid tenantId, Guid orderId, string sessionId, OrderStatus outcome, string? transferId,
        IConfiguration configuration, CancellationToken cancellationToken)
    {
        Order? paid = null;
        await scopes.InTenantAsync<OrderingDbContext>(tenantId, async db =>
        {
            var now = DateTimeOffset.UtcNow;
            DateTimeOffset? paidAt = outcome == OrderStatus.Paid ? now : null;
            var changed = await db.Orders
                .Where(o => o.Id == orderId && o.StripeCheckoutSessionId == sessionId && o.Status == OrderStatus.Pending)
                .ExecuteUpdateAsync(o => o
                    .SetProperty(x => x.Status, outcome)
                    .SetProperty(x => x.PaidAt, paidAt)
                    .SetProperty(x => x.StripeTransferId, transferId)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken);
            if (changed == 0 || outcome != OrderStatus.Paid)
            {
                return;
            }

            var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, cancellationToken);
            db.OutboxMessages.Add(OutboxMessage.For(OrderPaid.Queue, new OrderPaid(
                order.Id, order.TenantId, order.CustomerId, order.CustomerEmail, order.ProgramId, order.ProgramTitle, order.CoachSlug,
                order.Amount, order.Currency, order.PaidAt!.Value)));
            db.OutboxMessages.Add(OutboxMessage.For(EmailNotification.Queue, SaleEmail(order, configuration)));
            await db.SaveChangesAsync(cancellationToken);
            paid = order;
        }, cancellationToken);
        return paid;
    }
}
