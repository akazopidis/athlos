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
    public sealed record MyOrder(Guid Id, Guid ProgramId, string ProgramTitle, string CoachSlug, long Amount, string Currency, OrderStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? PaidAt, Guid? CheckoutId);

    // Status: Paid or Refunded (a refunded sale stays in the list, marked, and earns nothing).
    public sealed record Sale(Guid Id, Guid ProgramId, string ProgramTitle, string CustomerEmail, long Amount, long PlatformFee, string Currency, DateTimeOffset PaidAt, OrderStatus Status, DateTimeOffset? RefundedAt);

    public sealed record Paging([property: Range(1, 10_000)] int Page = 1, [property: Range(1, MaxPageSize)] int PageSize = 20);

    // API convention for paged lists (design §7.1). Two concrete records, not a generic Page<T>: the
    // OpenAPI generator names a generic schema after its definition, so both lists would share one schema.
    public sealed record MyOrdersPage(List<MyOrder> Items, int Page, int PageSize, int TotalCount);

    public sealed record SalesPage(List<Sale> Items, int Page, int PageSize, int TotalCount);

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
                        .Select(o => new MyOrder(o.Id, o.ProgramId, o.ProgramTitle, o.CoachSlug, o.Amount, o.Currency, o.Status, o.CreatedAt, o.PaidAt, o.CheckoutId))
                        .ToListAsync(cancellationToken),
                    paging.Page, paging.PageSize, await orders.CountAsync(cancellationToken)));
            })
            .WithName("ListMyOrders")
            .WithTags("Orders");

    // The coach's sales: the tenant filter does the scoping (RLS enforces it again).
    public static void MapListSales(this IEndpointRouteBuilder studio) =>
        studio.MapGet("/sales", async (string tenantSlug, [AsParameters] Paging paging, OrderingDbContext db, CancellationToken cancellationToken) =>
            {
                // The route's slug must match too, like tenancy's workspace endpoints: a mismatch is empty.
                // Refunded sales stay listed if they had paid the coach; a program paid twice, refunded
                // before any transfer, was never a sale.
                var sales = db.Orders.AsNoTracking().Where(o => (o.Status == OrderStatus.Paid || (o.Status == OrderStatus.Refunded && o.CheckoutId != null && o.StripeTransferId != null)) && o.CoachSlug == tenantSlug);

                return TypedResults.Ok(new SalesPage(
                    await sales.OrderByDescending(o => o.PaidAt)
                        .Skip((paging.Page - 1) * paging.PageSize).Take(paging.PageSize)
                        .Select(o => new Sale(o.Id, o.ProgramId, o.ProgramTitle, o.CustomerEmail, o.Amount, o.PlatformFee, o.Currency, o.PaidAt!.Value, o.Status, o.RefundedAt))
                        .ToListAsync(cancellationToken),
                    paging.Page, paging.PageSize, await sales.CountAsync(cancellationToken)));
            })
            .WithName("ListSales")
            .WithTags("Sales");
}
