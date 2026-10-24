using System.ComponentModel.DataAnnotations;
using Athlos.Ordering.Api.Data;
using Athlos.Ordering.Api.Features.Checkout;
using Athlos.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Ordering.Api.Features.Cart;

// GET and PUT /api/v1/me/cart (design §2.2, §8): the signed-in customer's cart, so it follows them
// across devices. The browser sends the whole cart after every change (at most as many programs as one
// checkout takes); the last write wins. The customer's own row (RLS: customer_id = app.user_id), not a
// cross-tenant slice: carts have no tenant. Display only: checkout prices every program again.
public static class MyCart
{
    public sealed record Line(
        Guid ProgramId,
        [property: Required, MaxLength(63)] string CoachSlug,
        [property: Required, MaxLength(100)] string Slug,
        [property: Required, MaxLength(200)] string Title,
        [property: Range(0, long.MaxValue)] long PriceAmount,
        [property: Required, StringLength(3, MinimumLength = 3)] string Currency,
        [property: MaxLength(40)] string? Category);

    public sealed record Request([property: Required, MaxLength(StartCheckout.MaxPrograms)] List<Line> Items);

    public sealed record Response(List<Line> Items, DateTimeOffset? UpdatedAt);

    public static void MapMyCart(this IEndpointRouteBuilder me)
    {
        me.MapGet("/cart", Get).WithName("GetMyCart").WithTags("Cart");
        me.MapPut("/cart", Put).WithName("PutMyCart").WithTags("Cart");
    }

    private static async Task<Ok<Response>> Get(ITenantContext tenant, OrderingDbContext db, CancellationToken cancellationToken)
    {
        var customerId = tenant.UserId!.Value;
        var cart = await db.Carts.AsNoTracking().SingleOrDefaultAsync(c => c.CustomerId == customerId, cancellationToken);
        return TypedResults.Ok(ToResponse(cart));
    }

    private static async Task<Ok<Response>> Put(Request request, ITenantContext tenant, OrderingDbContext db, CancellationToken cancellationToken)
    {
        // A program once, in the order it was added.
        var lines = request.Items.DistinctBy(i => i.ProgramId).Select(i => new CartLine
        {
            ProgramId = i.ProgramId,
            CoachSlug = i.CoachSlug,
            Slug = i.Slug,
            Title = i.Title,
            PriceAmount = i.PriceAmount,
            Currency = i.Currency,
            Category = i.Category,
        }).ToList();

        var customerId = tenant.UserId!.Value;
        var cart = await db.Carts.SingleOrDefaultAsync(c => c.CustomerId == customerId, cancellationToken);
        if (cart is null)
        {
            cart = new CustomerCart { CustomerId = customerId };
            db.Carts.Add(cart);
        }

        cart.Lines = lines;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ToResponse(cart));
    }

    private static Response ToResponse(CustomerCart? cart) =>
        new([.. (cart?.Lines ?? []).Select(l => new Line(l.ProgramId, l.CoachSlug, l.Slug, l.Title, l.PriceAmount, l.Currency, l.Category))], cart?.UpdatedAt);
}
