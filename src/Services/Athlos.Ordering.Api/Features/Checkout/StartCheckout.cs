using System.ComponentModel.DataAnnotations;
using Athlos.Ordering.Api.Data;
using Athlos.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Athlos.Ordering.Api.Features.Checkout;

// A signed-in customer buys a published program (design §2.2). The order belongs to the coach's
// tenant, which the customer is not a member of.
// CROSS-TENANT SLICE (design §3.2): the tenant comes from catalog's offer for the program (a signed
// service-to-service answer), never from the request. The slice enters that tenant before the
// transaction, so RLS checks the insert like any other.
public static class StartCheckout
{
    public sealed record Request([property: Required] Guid ProgramId);

    public sealed record Response(Guid OrderId, string CheckoutUrl);

    public static void MapStartCheckout(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/v1/orders/checkout", Handle)
            .RequireAuthorization()
            .WithName("StartCheckout")
            .WithTags("Orders");

    private static async Task<Results<Ok<Response>, NotFound, ProblemHttpResult>> Handle(
        Request request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        HttpContext http,
        TenantContext tenant,
        OrderingDbContext db,
        ISellerDirectory sellers,
        ICheckoutSessions checkout,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var email = http.User.FindFirst("email")?.Value;
        if (tenant.UserId is not { } customerId || string.IsNullOrWhiteSpace(email))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized);
        }

        if (idempotencyKey is { Length: > 100 })
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Idempotency-Key is at most 100 characters.");
        }

        if (!checkout.IsConfigured)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Payments are not configured for this environment.");
        }

        if (await sellers.GetOfferAsync(request.ProgramId, cancellationToken) is not { } offer)
        {
            return TypedResults.NotFound();
        }

        var seller = await sellers.GetPayoutAccountAsync(offer.TenantId, cancellationToken);
        if (seller is not { CanSell: true })
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "This coach cannot take payments right now.");
        }

        tenant.EnterTenant(offer.TenantId, role: null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Retrying with the same key returns the same order and checkout page (design §7.1).
        if (idempotencyKey is not null
            && await db.Orders.SingleOrDefaultAsync(o => o.CustomerId == customerId && o.IdempotencyKey == idempotencyKey, cancellationToken) is { } existing)
        {
            return existing is { Status: OrderStatus.Pending, CheckoutUrl: { } url }
                ? TypedResults.Ok(new Response(existing.Id, url))
                : TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "This checkout has already finished.");
        }

        if (await db.Orders.AnyAsync(o => o.CustomerId == customerId && o.ProgramId == offer.ProgramId && o.Status == OrderStatus.Paid, cancellationToken))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "You already own this program.");
        }

        var order = new Order
        {
            CustomerId = customerId,
            CustomerEmail = email,
            ProgramId = offer.ProgramId,
            ProgramTitle = offer.Title,
            CoachSlug = offer.CoachSlug,
            Amount = offer.PriceAmount,
            Currency = offer.Currency,
            PlatformFee = PlatformFee(offer.PriceAmount, configuration),
            IdempotencyKey = idempotencyKey,
        };
        db.Orders.Add(order);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "A checkout with this Idempotency-Key is already in progress.");
        }

        var publicUrl = new Uri(configuration["App:PublicUrl"] ?? throw new InvalidOperationException("App:PublicUrl is not configured."));
        var session = await checkout.CreateAsync(order, seller.StripeAccountId!,
            successUrl: new Uri(publicUrl, $"/me?checkout=success&order={order.Id}"),
            cancelUrl: new Uri(publicUrl, $"/c/{offer.CoachSlug}?checkout=cancelled"),
            cancellationToken);

        order.StripeCheckoutSessionId = session.Id;
        order.CheckoutUrl = session.Url;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return TypedResults.Ok(new Response(order.Id, session.Url));
    }

    // The platform's share, in the order's minor units, rounded down (design §0: "the platform keeps a fee").
    public static long PlatformFee(long amount, IConfiguration configuration)
    {
        var percent = configuration.GetValue("Payments:PlatformFeePercent", 10);
        return amount * percent / 100;
    }
}
