using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Athlos.Ordering.Api.Features.Checkout;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Athlos.Ordering.Api.Features.Discounts;

// POST /api/v1/orders/discount (design §2.4): the cart asks what a discount code takes off its
// programs before checkout, which applies it the same way. Each program's coach comes from catalog's
// signed offer, never from the request, and the code is looked up inside each coach's tenant, so it
// applies only to the programs of coaches who created it. A code that applies to nothing is 404.
// Counted with checkouts by the gateway's per-customer limit, so codes cannot be guessed quickly.
public static class CheckDiscount
{
    public sealed record Request(
        [property: Required, MaxLength(40)] string Code,
        [property: Required, MinLength(1), MaxLength(StartCheckout.MaxPrograms)] List<Guid> ProgramIds);

    public sealed record Line(Guid ProgramId, int PercentOff, long DiscountAmount);

    public sealed record Response(string Code, List<Line> Lines);

    public static void MapCheckDiscount(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/v1/orders/discount", Handle)
            .RequireAuthorization()
            .WithName("CheckDiscount")
            .WithTags("Orders");

    private static async Task<Results<Ok<Response>, ProblemHttpResult>> Handle(
        Request request, ISellerDirectory sellers, IServiceScopeFactory scopes, TimeProvider time, CancellationToken cancellationToken)
    {
        var code = DiscountRules.Normalize(request.Code);
        var offers = new List<ProgramOffer>();
        if (Regex.IsMatch(code, DiscountRules.CodePattern))
        {
            foreach (var programId in request.ProgramIds.Distinct())
            {
                if (await sellers.GetOfferAsync(programId, cancellationToken) is { } offer)
                {
                    offers.Add(offer);
                }
            }
        }

        var applied = await DiscountRules.ApplyAsync(scopes, offers, code, time.GetUtcNow(), cancellationToken);
        if (applied.Count == 0)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "This code does not apply to the programs in your cart.");
        }

        return TypedResults.Ok(new Response(code, offers
            .Where(o => applied.ContainsKey(o.ProgramId))
            .Select(o => new Line(o.ProgramId, applied[o.ProgramId].PercentOff, DiscountRules.Amount(o.PriceAmount, applied[o.ProgramId].PercentOff)))
            .ToList()));
    }
}
