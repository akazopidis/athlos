using System.ComponentModel.DataAnnotations;
using Athlos.Ordering.Api.Data;
using Athlos.Tenancy;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Ordering.Api.Features.Orders;

// The two views of the same rows (design §1, the B2B2C rule):
// - a customer sees all of their own orders, across coaches;
// - a coach sees their tenant's sales, and nothing of other tenants.
public static class OrderLists
{
    public const int MaxPageSize = 50;

    // CheckoutId: the cart checkout it was bought in (null before the cart), so the page Stripe returns
    // to can find the orders of that payment.
    // DiscountCode and DiscountAmount: a coach's code and what it took off (design §2.4); Amount is what was paid.
    public sealed record MyOrder(
        Guid Id, Guid ProgramId, string ProgramTitle, string CoachSlug, long Amount, string Currency, OrderStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? PaidAt,
        Guid? CheckoutId, string? DiscountCode, long DiscountAmount);

    // Status: Paid or Refunded (a refunded sale stays in the list, marked, and earns nothing).
    // RefundableUntil: the end of the coach's refund window (Payments:RefundWindowDays after payment).
    // CustomerEmail: null once the customer deleted their account (design §5.2).
    public sealed record Sale(
        Guid Id, Guid ProgramId, string ProgramTitle, string? CustomerEmail, long Amount, long PlatformFee, string Currency, DateTimeOffset PaidAt,
        OrderStatus Status, DateTimeOffset? RefundedAt, DateTimeOffset RefundableUntil, string? DiscountCode);

    public sealed record Paging([property: Range(1, 10_000)] int Page = 1, [property: Range(1, MaxPageSize)] int PageSize = 20);

    // API convention for paged lists (design §7.1). Two concrete records, not a generic Page<T>: the
    // OpenAPI generator names a generic schema after its definition, so both lists would share one schema.
    public sealed record MyOrdersPage(List<MyOrder> Items, int Page, int PageSize, int TotalCount);

    public sealed record SalesPage(List<Sale> Items, int Page, int PageSize, int TotalCount);

    // A receipt for one checkout (design §5.4): each program with its price before and after any
    // discount, and the total paid. Refunded lines stay, marked, with the amount given back.
    public sealed record ReceiptLine(
        Guid OrderId, string ProgramTitle, string CoachSlug, long Price, string? DiscountCode, long DiscountAmount, long Amount, OrderStatus Status, DateTimeOffset? RefundedAt);

    public sealed record Receipt(
        Guid CheckoutId, DateTimeOffset PaidAt, string Currency, List<ReceiptLine> Lines, long Subtotal, long Discount, long Total, long Refunded);

    // CROSS-TENANT SLICE (design §3.2, "a customer's own orders"): ignores the tenant filter and
    // relies on the customer filter below and on RLS, which reads orders where customer_id = app.user_id.
    public static void MapListMyOrders(this IEndpointRouteBuilder me) =>
        me.MapGet("/orders", async ([AsParameters] Paging paging, ITenantContext tenant, OrderingDbContext db, CancellationToken cancellationToken) =>
            {
                var customerId = tenant.UserId;
                var orders = db.Orders.AsNoTracking()
                    .IgnoreQueryFilters([TenantDbContext.TenantFilter])
                    .Where(o => o.CustomerId == customerId && o.Status != OrderStatus.Failed);

                return TypedResults.Ok(new MyOrdersPage(
                    await orders.OrderByDescending(o => o.CreatedAt)
                        .Skip((paging.Page - 1) * paging.PageSize).Take(paging.PageSize)
                        .Select(o => new MyOrder(o.Id, o.ProgramId, o.ProgramTitle, o.CoachSlug, o.Amount, o.Currency, o.Status, o.CreatedAt, o.PaidAt, o.CheckoutId, o.DiscountCode, o.DiscountAmount))
                        .ToListAsync(cancellationToken),
                    paging.Page, paging.PageSize, await orders.CountAsync(cancellationToken)));
            })
            .WithName("ListMyOrders")
            .WithTags("Orders");

    // GET /api/v1/me/orders/receipts/{checkoutId}: the receipt of one of the customer's checkouts, once
    // paid. Same cross-tenant slice as the list: the customer's own orders, across coaches. Another
    // customer's checkout, or one not paid, is 404.
    public static void MapGetMyReceipt(this IEndpointRouteBuilder me) =>
        me.MapGet("/orders/receipts/{checkoutId:guid}", async Task<Results<Ok<Receipt>, NotFound>> (Guid checkoutId, ITenantContext tenant, OrderingDbContext db, CancellationToken cancellationToken) =>
            {
                var customerId = tenant.UserId;
                var lines = await db.Orders.AsNoTracking()
                    .IgnoreQueryFilters([TenantDbContext.TenantFilter])
                    .Where(o => o.CustomerId == customerId && o.CheckoutId == checkoutId && o.PaidAt != null
                        && (o.Status == OrderStatus.Paid || o.Status == OrderStatus.Refunded))
                    .OrderBy(o => o.CreatedAt).ThenBy(o => o.Id)
                    .Select(o => new { o.Id, o.ProgramTitle, o.CoachSlug, o.Amount, o.DiscountCode, o.DiscountAmount, o.Status, o.RefundedAt, o.PaidAt, o.Currency })
                    .ToListAsync(cancellationToken);
                if (lines.Count == 0)
                {
                    return TypedResults.NotFound();
                }

                return TypedResults.Ok(new Receipt(
                    checkoutId, lines.Min(l => l.PaidAt!.Value), lines[0].Currency,
                    [.. lines.Select(l => new ReceiptLine(l.Id, l.ProgramTitle, l.CoachSlug, l.Amount + l.DiscountAmount, l.DiscountCode, l.DiscountAmount, l.Amount, l.Status, l.RefundedAt))],
                    lines.Sum(l => l.Amount + l.DiscountAmount), lines.Sum(l => l.DiscountAmount), lines.Sum(l => l.Amount),
                    lines.Where(l => l.Status == OrderStatus.Refunded).Sum(l => l.Amount)));
            })
            .WithName("GetMyReceipt")
            .WithTags("Orders");

    // The coach's sales: the tenant filter does the scoping (RLS enforces it again).
    public static void MapListSales(this IEndpointRouteBuilder studio) =>
        studio.MapGet("/sales", async (string tenantSlug, [AsParameters] Paging paging, OrderingDbContext db, IConfiguration configuration, CancellationToken cancellationToken) =>
            {
                var window = Refunds.OrderRefunds.RefundWindow(configuration);
                // The route's slug must match too, like tenancy's workspace endpoints: a mismatch is empty.
                // Refunded sales stay listed if they had paid the coach; a program paid twice, refunded
                // before any transfer, was never a sale.
                var sales = db.Orders.AsNoTracking().Where(o => (o.Status == OrderStatus.Paid || (o.Status == OrderStatus.Refunded && o.CheckoutId != null && o.StripeTransferId != null)) && o.CoachSlug == tenantSlug);

                return TypedResults.Ok(new SalesPage(
                    await sales.OrderByDescending(o => o.PaidAt)
                        .Skip((paging.Page - 1) * paging.PageSize).Take(paging.PageSize)
                        .Select(o => new Sale(o.Id, o.ProgramId, o.ProgramTitle, o.CustomerEmail == Customers.EraseCustomer.ErasedEmail ? null : o.CustomerEmail, o.Amount, o.PlatformFee, o.Currency, o.PaidAt!.Value, o.Status, o.RefundedAt,
                            o.PaidAt!.Value + window, o.DiscountCode))
                        .ToListAsync(cancellationToken),
                    paging.Page, paging.PageSize, await sales.CountAsync(cancellationToken)));
            })
            .WithName("ListSales")
            .WithTags("Sales");
}
