using Athlos.Ordering.Api.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Ordering.Api.Features.Orders;

// The studio overview's numbers (design §8): sales and earnings (after the platform fee) this calendar
// month (UTC) and in total, distinct customers, and the best-selling program. Paid sales only: a
// refunded one earned nothing. Scoped like the sales list: the tenant filter (and RLS) give this tenant's
// orders, and the route's slug must match, so another coach's address gives zeros.
public static class SalesSummary
{
    public sealed record Response(
        string Currency, int SalesThisMonth, long EarningsThisMonth, int Sales, long Earnings, int Customers, string? BestSeller, int BestSellerSales);

    public static void MapSalesSummary(this IEndpointRouteBuilder studio) =>
        studio.MapGet("/sales/summary", Handle)
            .WithName("GetSalesSummary")
            .WithTags("Sales");

    private static async Task<Ok<Response>> Handle(string tenantSlug, OrderingDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var paid = db.Orders.AsNoTracking().Where(o => o.Status == OrderStatus.Paid && o.CoachSlug == tenantSlug);

        var totals = await paid
            .GroupBy(o => 1)
            .Select(g => new
            {
                Sales = g.Count(),
                Earnings = g.Sum(o => o.Amount - o.PlatformFee),
                SalesThisMonth = g.Count(o => o.PaidAt >= monthStart),
                EarningsThisMonth = g.Where(o => o.PaidAt >= monthStart).Sum(o => o.Amount - o.PlatformFee),
                Customers = g.Select(o => o.CustomerId).Distinct().Count(),
            })
            .SingleOrDefaultAsync(cancellationToken);
        var best = await paid
            .GroupBy(o => o.ProgramTitle)
            .OrderByDescending(g => g.Count())
            .Select(g => new { Title = g.Key, Sales = g.Count() })
            .FirstOrDefaultAsync(cancellationToken);

        return TypedResults.Ok(new Response(
            "EUR", totals?.SalesThisMonth ?? 0, totals?.EarningsThisMonth ?? 0, totals?.Sales ?? 0, totals?.Earnings ?? 0, totals?.Customers ?? 0,
            best?.Title, best?.Sales ?? 0));
    }
}
