using Athlos.Messaging;
using Athlos.Messaging.Contracts;
using Athlos.Ordering.Api.Data;
using Athlos.Ordering.Api.Features.Checkout;
using Athlos.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Stripe.Checkout;

namespace Athlos.Ordering.Api.Features.Webhooks;

// Stripe calls POST /webhooks/stripe through the gateway (design §2.2, §5.1):
// 1. verify the Stripe-Signature and reject events older than five minutes;
// 2. find the tenant in the session's metadata (written by checkout), enter it, so RLS applies;
// 3. in one transaction: record the event id (dedupe), mark the order Paid, write OrderPaid and the
//    coach's "you made a sale" email to the outbox. Either all of it commits or none of it, and a
//    retried event changes nothing.
public static class StripeWebhook
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
        HttpRequest request, StripeWebhookVerifier verifier, TenantContext tenant, OrderingDbContext db, IConfiguration configuration,
        CancellationToken cancellationToken)
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
        if (outcome is null || stripeEvent.Data.Object is not Session session
            || !Guid.TryParse(session.Metadata?.GetValueOrDefault(CheckoutMetadata.TenantId), out var tenantId)
            || !Guid.TryParse(session.Metadata?.GetValueOrDefault(CheckoutMetadata.OrderId), out var orderId))
        {
            return TypedResults.Ok();
        }

        // A completed session may still be waiting for a delayed payment method.
        if (outcome == OrderStatus.Paid && session.PaymentStatus != "paid")
        {
            return TypedResults.Ok();
        }

        tenant.EnterTenant(tenantId, role: null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        db.ProcessedStripeEvents.Add(new ProcessedStripeEvent { StripeEventId = stripeEvent.Id });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return TypedResults.Ok(); // Already handled: Stripe retried.
        }

        var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == orderId && o.StripeCheckoutSessionId == session.Id, cancellationToken);
        var paid = false;
        if (order is { Status: OrderStatus.Pending })
        {
            paid = outcome == OrderStatus.Paid;
            order.Status = outcome.Value;
            if (outcome == OrderStatus.Paid)
            {
                order.PaidAt = DateTimeOffset.UtcNow;
                db.OutboxMessages.Add(OutboxMessage.For(OrderPaid.Queue, new OrderPaid(
                    order.Id, order.TenantId, order.CustomerId, order.CustomerEmail, order.ProgramId, order.ProgramTitle, order.CoachSlug,
                    order.Amount, order.Currency, order.PaidAt.Value)));
                db.OutboxMessages.Add(OutboxMessage.For(EmailNotification.Queue, SaleEmail(order, configuration)));
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        // Counted once the order is committed as Paid; a retried event never reaches this point.
        if (paid)
        {
            OrderMetrics.OrderPaid(order!.TenantId, order.Currency);
        }

        return TypedResults.Ok();
    }
}
