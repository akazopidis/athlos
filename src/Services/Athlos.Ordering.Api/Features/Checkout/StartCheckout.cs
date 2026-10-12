using System.ComponentModel.DataAnnotations;
using Athlos.Ordering.Api.Data;
using Athlos.Tenancy;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Athlos.Ordering.Api.Features.Checkout;

// A signed-in customer buys the programs in their cart, from any number of coaches, with one payment
// (design §2.2). "Buy now" is a cart of one.
//
// 1. Every program must be on sale (catalog's offer), in one currency, from a coach who can take
//    payments (tenancy), not from a workspace the buyer works in (a coach's own programs are theirs in
//    the studio), and not already owned. Otherwise nothing is created, and the problem names
//    the programIds at fault and a reason (notForSale, currencies, cannotSell, ownWorkspace, owned), so the cart
//    can drop or flag them.
// 2. Stripe creates one Checkout Session for all of them (separate charges and transfers).
// 3. The customer's Checkout row (the Idempotency-Key) and one Order per program are written; each
//    order inside its coach's tenant, one tenant at a time, so the EF filter and RLS check every
//    insert. The tenant comes from catalog's signed offer, never from the request.
// The session is created before the rows, so a failure leaves no order pending without a payment
// page; an unused session expires at Stripe.
// A program is paid for once: a new checkout closes the customer's older, still-open Stripe pages for
// the same programs first (found on dev: three open pages for one program, two of them paid). If one
// of them was paid meanwhile, the program counts as owned. The webhook refunds what still slips through.
public static class StartCheckout
{
    public const int MaxPrograms = 10;

    public sealed record Request([property: Required, MinLength(1), MaxLength(MaxPrograms)] List<Guid> ProgramIds);

    public sealed record Response(Guid CheckoutId, string CheckoutUrl);

    public static void MapStartCheckout(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/v1/orders/checkout", Handle)
            .RequireAuthorization()
            .WithName("StartCheckout")
            .WithTags("Orders");

    private static async Task<Results<Ok<Response>, ProblemHttpResult>> Handle(
        Request request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        HttpContext http,
        TenantContext tenant,
        OrderingDbContext db,
        ISellerDirectory sellers,
        ICheckoutSessions checkout,
        IServiceScopeFactory scopes,
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

        // Retrying with the same key returns the same checkout and Stripe page (design §7.1). The
        // Checkout row is the customer's own (RLS: customer_id = app.user_id).
        if (idempotencyKey is not null && await FindCheckoutAsync(db, customerId, idempotencyKey, cancellationToken) is { } existing)
        {
            return TypedResults.Ok(existing);
        }

        var programIds = request.ProgramIds.Distinct().ToList();
        var offers = new List<ProgramOffer>();
        foreach (var programId in programIds)
        {
            if (await sellers.GetOfferAsync(programId, cancellationToken) is { } offer)
            {
                offers.Add(offer);
            }
        }

        if (offers.Count < programIds.Count)
        {
            return Problem(StatusCodes.Status404NotFound, "notForSale", "Some programs in your cart are no longer for sale.",
                programIds.Except(offers.Select(o => o.ProgramId)));
        }

        if (offers.Select(o => o.Currency).Distinct().Count() > 1)
        {
            return Problem(StatusCodes.Status400BadRequest, "currencies", "Programs in different currencies are paid separately.", programIds);
        }

        var accounts = new Dictionary<Guid, string>();
        foreach (var coach in offers.GroupBy(o => o.TenantId))
        {
            var seller = await sellers.GetPayoutAccountAsync(coach.Key, cancellationToken);
            if (seller is not { CanSell: true })
            {
                return Problem(StatusCodes.Status409Conflict, "cannotSell", $"{coach.First().CoachSlug} cannot take payments right now.", coach.Select(o => o.ProgramId));
            }

            accounts[coach.Key] = seller.StripeAccountId!;

            if (await sellers.IsMemberAsync(coach.First().CoachSlug, customerId, cancellationToken))
            {
                return Problem(StatusCodes.Status409Conflict, "ownWorkspace", "You cannot buy programs from a workspace you coach in.", coach.Select(o => o.ProgramId));
            }
        }

        var owned = new List<Guid>();
        foreach (var coach in offers.GroupBy(o => o.TenantId))
        {
            var ids = coach.Select(o => o.ProgramId).ToList();
            await scopes.InTenantAsync<OrderingDbContext>(coach.Key, async orders => owned.AddRange(await orders.Orders
                .Where(o => o.CustomerId == customerId && ids.Contains(o.ProgramId) && o.Status == OrderStatus.Paid)
                .Select(o => o.ProgramId)
                .ToListAsync(cancellationToken)), cancellationToken);
        }

        if (owned.Count > 0)
        {
            return Problem(StatusCodes.Status409Conflict, "owned", "You already own some of these programs.", owned.Distinct());
        }

        var superseded = new List<(string SessionId, Guid ProgramId)>();
        foreach (var coach in offers.GroupBy(o => o.TenantId))
        {
            var ids = coach.Select(o => o.ProgramId).ToList();
            await scopes.InTenantAsync<OrderingDbContext>(coach.Key, async orders => superseded.AddRange((await orders.Orders
                .Where(o => o.CustomerId == customerId && ids.Contains(o.ProgramId) && o.Status == OrderStatus.Pending && o.StripeCheckoutSessionId != null)
                .Select(o => new { o.StripeCheckoutSessionId, o.ProgramId })
                .ToListAsync(cancellationToken)).Select(o => (o.StripeCheckoutSessionId!, o.ProgramId))), cancellationToken);
        }

        var paidMeanwhile = new List<Guid>();
        foreach (var older in superseded.GroupBy(s => s.SessionId))
        {
            if (!await checkout.ExpireAsync(older.Key, cancellationToken))
            {
                paidMeanwhile.AddRange(older.Select(s => s.ProgramId));
            }
        }

        if (paidMeanwhile.Count > 0)
        {
            return Problem(StatusCodes.Status409Conflict, "owned", "A payment for some of these programs has just gone through. See My orders.", paidMeanwhile.Distinct());
        }

        var checkoutId = Guid.CreateVersion7();
        var newOrders = offers.Select(offer => new Order
        {
            TenantId = offer.TenantId,
            CustomerId = customerId,
            CustomerEmail = email,
            ProgramId = offer.ProgramId,
            ProgramTitle = offer.Title,
            CoachSlug = offer.CoachSlug,
            Amount = offer.PriceAmount,
            Currency = offer.Currency,
            PlatformFee = PlatformFee(offer.PriceAmount, configuration),
            CheckoutId = checkoutId,
            SellerAccountId = accounts[offer.TenantId],
        }).ToList();

        var publicUrl = new Uri(configuration["App:PublicUrl"] ?? throw new InvalidOperationException("App:PublicUrl is not configured."));
        var session = await checkout.CreateAsync(new CheckoutRequest(checkoutId, email, offers[0].Currency, newOrders,
            SuccessUrl: new Uri(publicUrl, $"/me?checkout=success&id={checkoutId}"),
            CancelUrl: new Uri(publicUrl, "/cart?checkout=cancelled")), cancellationToken);

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            db.Checkouts.Add(new Data.Checkout
            {
                Id = checkoutId,
                CustomerId = customerId,
                IdempotencyKey = idempotencyKey,
                StripeCheckoutSessionId = session.Id,
                CheckoutUrl = session.Url,
            });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "A checkout with this Idempotency-Key is already in progress.");
            }

            await transaction.CommitAsync(cancellationToken);
        }

        foreach (var coach in newOrders.GroupBy(o => o.TenantId))
        {
            await scopes.InTenantAsync<OrderingDbContext>(coach.Key, async orders =>
            {
                foreach (var order in coach)
                {
                    order.StripeCheckoutSessionId = session.Id;
                    order.CheckoutUrl = session.Url;
                    orders.Orders.Add(order);
                }

                await orders.SaveChangesAsync(cancellationToken);
            }, cancellationToken);
        }

        return TypedResults.Ok(new Response(checkoutId, session.Url));
    }

    private static async Task<Response?> FindCheckoutAsync(OrderingDbContext db, Guid customerId, string idempotencyKey, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        return await db.Checkouts.AsNoTracking()
            .Where(c => c.CustomerId == customerId && c.IdempotencyKey == idempotencyKey)
            .Select(c => new Response(c.Id, c.CheckoutUrl))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static ProblemHttpResult Problem(int status, string reason, string title, IEnumerable<Guid> programIds) =>
        TypedResults.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?>
        {
            ["reason"] = reason,
            ["programIds"] = programIds.ToList(),
        });

    // The platform's share, in the order's minor units, rounded down (design §0: "the platform keeps a fee").
    public static long PlatformFee(long amount, IConfiguration configuration)
    {
        var percent = configuration.GetValue("Payments:PlatformFeePercent", 10);
        return amount * percent / 100;
    }
}
