using Athlos.Catalog.Api.Data;

namespace Athlos.Catalog.Api.Features.Marketplace;

// The code a coach shows on a program (design §2.4): cards and the program page say "20% off with
// SUMMER". Checkout applies the code, not this; it is only an advert.
public sealed record DiscountOffer(string Code, int PercentOff, DateTimeOffset? ExpiresAt)
{
    // The workspace's shown code, if it covers this program (every program of the workspace, or this one).
    public static DiscountOffer? For(Guid programId, PublicDiscount? discount) =>
        discount is not null && (discount.ProgramId is null || discount.ProgramId == programId)
            ? new DiscountOffer(discount.Code, discount.PercentOff, discount.ExpiresAt)
            : null;
}
