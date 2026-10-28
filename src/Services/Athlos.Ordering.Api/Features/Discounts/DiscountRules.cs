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
    // when it applies to none: unknown, switched off, expired, used up, for another program, or already
    // used by this customer (once per customer, backlog item 48). A code with uses left applies to at
    // most that many programs; orders paid or still waiting on an open payment page count as uses.
    public static async Task<Applied?> FindAsync(
        OrderingDbContext db, string code, IReadOnlyList<Guid> programIds, Guid customerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var discount = await db.DiscountCodes.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Code == code && d.IsActive && (d.ExpiresAt == null || d.ExpiresAt > now), cancellationToken);
        if (discount is null || await UsedByAsync(db, discount.Code, customerId, cancellationToken))
        {
            return null;
        }

        IEnumerable<Guid> matching = programIds.Where(id => discount.ProgramId is null || discount.ProgramId == id);
        if (discount.MaxRedemptions is { } max)
        {
            matching = matching.Take(Math.Max(0, max - await UsesAsync(db, discount.Code, customerId, programIds, cancellationToken)));
        }

        var applied = matching.ToList();
        return applied.Count == 0 ? null : new Applied(discount.Code, discount.PercentOff, applied);
    }

    // At checkout, inside the transaction that writes the coach's orders: takes the code's lock, so
    // buyers of one code's last use queue up, and confirms the uses are still there. Without it, two
    // checkouts priced at the same moment could both get the last use (backlog item 48).
    public static async Task<bool> ClaimAsync(
        OrderingDbContext db, string code, Guid customerId, IReadOnlyList<Guid> programIds, int uses, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"discount:" + code}, 0))", cancellationToken);
        var discount = await db.DiscountCodes.AsNoTracking().SingleOrDefaultAsync(d => d.Code == code && d.IsActive, cancellationToken);
        return discount is not null
            && !await UsedByAsync(db, code, customerId, cancellationToken)
            && (discount.MaxRedemptions is not { } max || await UsesAsync(db, code, customerId, programIds, cancellationToken) + uses <= max);
    }

    // Once per customer: a paid order with the code is a use, a refunded one is not.
    private static Task<bool> UsedByAsync(OrderingDbContext db, string code, Guid customerId, CancellationToken cancellationToken) =>
        db.Orders.AnyAsync(o => o.CustomerId == customerId && o.DiscountCode == code && o.Status == OrderStatus.Paid, cancellationToken);

    // Uses taken: paid orders and open payment pages, except this customer's own open pages for these
    // programs, which their new checkout closes.
    private static Task<int> UsesAsync(
        OrderingDbContext db, string code, Guid customerId, IReadOnlyList<Guid> programIds, CancellationToken cancellationToken) =>
        db.Orders.CountAsync(
            o => o.DiscountCode == code
                && (o.Status == OrderStatus.Paid
                    || (o.Status == OrderStatus.Pending && !(o.CustomerId == customerId && programIds.Contains(o.ProgramId)))),
            cancellationToken);

    // The code applied to a cart: for each program it takes something off, the code and percentage.
    // Looked up inside each coach's tenant in the cart, one tenant at a time.
    public static async Task<Dictionary<Guid, Applied>> ApplyAsync(
        IServiceScopeFactory scopes, IReadOnlyList<ProgramOffer> offers, string code, Guid customerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var applied = new Dictionary<Guid, Applied>();
        foreach (var coach in offers.GroupBy(o => o.TenantId))
        {
            var ids = coach.Select(o => o.ProgramId).ToList();
            await scopes.InTenantAsync<OrderingDbContext>(coach.Key, async db =>
            {
                if (await FindAsync(db, code, ids, customerId, now, cancellationToken) is { } found)
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
