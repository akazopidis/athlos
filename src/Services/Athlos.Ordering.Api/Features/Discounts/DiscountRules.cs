using Athlos.Ordering.Api.Data;
using Athlos.Ordering.Api.Features.Checkout;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Ordering.Api.Features.Discounts;

// Applying a discount code (design §2.4), shared by the cart's price check and checkout so both apply
// it the same way. Call inside one coach's tenant (TenantScopes.InTenantAsync): the tenant filter and
// RLS make the lookup find that coach's codes only.
public static class DiscountRules
{
    public const string CodePattern = "^[A-Z0-9-]{3,20}$";

    public sealed record Applied(string Code, int PercentOff, IReadOnlyList<Guid> ProgramIds);

    public static string Normalize(string code) => code.Trim().ToUpperInvariant();

    // The discount, rounded down, in the price's minor units.
    public static long Amount(long price, int percentOff) => price * percentOff / 100;

    // Which of these programs (the coach's, in the cart's order) the code takes percentOff from, or null
    // when it applies to none: unknown, switched off, expired, used up, or for another program. A code
    // with uses left applies to at most that many programs; orders paid or still waiting on an open
    // payment page count as uses.
    public static async Task<Applied?> FindAsync(
        OrderingDbContext db, string code, IReadOnlyList<Guid> programIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var discount = await db.DiscountCodes.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Code == code && d.IsActive && (d.ExpiresAt == null || d.ExpiresAt > now), cancellationToken);
        if (discount is null)
        {
            return null;
        }

        IEnumerable<Guid> matching = programIds.Where(id => discount.ProgramId is null || discount.ProgramId == id);
        if (discount.MaxRedemptions is { } max)
        {
            var used = await db.Orders.CountAsync(
                o => o.DiscountCode == discount.Code && (o.Status == OrderStatus.Paid || o.Status == OrderStatus.Pending), cancellationToken);
            matching = matching.Take(Math.Max(0, max - used));
        }

        var applied = matching.ToList();
        return applied.Count == 0 ? null : new Applied(discount.Code, discount.PercentOff, applied);
    }

    // The code applied to a cart: for each program it takes something off, the code and percentage.
    // Looked up inside each coach's tenant in the cart, one tenant at a time.
    public static async Task<Dictionary<Guid, Applied>> ApplyAsync(
        IServiceScopeFactory scopes, IReadOnlyList<ProgramOffer> offers, string code, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var applied = new Dictionary<Guid, Applied>();
        foreach (var coach in offers.GroupBy(o => o.TenantId))
        {
            var ids = coach.Select(o => o.ProgramId).ToList();
            await scopes.InTenantAsync<OrderingDbContext>(coach.Key, async db =>
            {
                if (await FindAsync(db, code, ids, now, cancellationToken) is { } found)
                {
                    foreach (var id in found.ProgramIds)
                    {
                        applied[id] = found;
                    }
                }
            }, cancellationToken);
        }

        return applied;
    }
}
