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

    public sealed record MyOrder(Guid Id, Guid ProgramId, string ProgramTitle, string CoachSlug, long Amount, string Currency, OrderStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? PaidAt);

    public sealed record Sale(Guid Id, Guid ProgramId, string ProgramTitle, string CustomerEmail, long Amount, long PlatformFee, string Currency, DateTimeOffset PaidAt);

    public sealed record Paging([property: Range(1, 10_000)] int Page = 1, [property: Range(1, MaxPageSize)] int PageSize = 20);

    public sealed record Page<T>(List<T> Items, int PageNumber, int PageSize, int TotalCount);

    // CROSS-TENANT SLICE (design §3.2, "a customer's own orders"): ignores the tenant filter and
    // relies on the customer filter below and on RLS, which reads orders where customer_id = app.user_id.
    public static void MapListMyOrders(this IEndpointRouteBuilder me) =>
        me.MapGet("/orders", async ([AsParameters] Paging paging, ITenantContext tenant, OrderingDbContext db, CancellationToken cancellationToken) =>
            {
                var customerId = tenant.UserId;
                var orders = db.Orders.AsNoTracking()
                    .IgnoreQueryFilters([TenantDbContext.TenantFilter])
                    .Where(o => o.CustomerId == customerId && o.Status != OrderStatus.Failed);

                return TypedResults.Ok(new Page<MyOrder>(
                    await orders.OrderByDescending(o => o.CreatedAt)
                        .Skip((paging.Page - 1) * paging.PageSize).Take(paging.PageSize)
                        .Select(o => new MyOrder(o.Id, o.ProgramId, o.ProgramTitle, o.CoachSlug, o.Amount, o.Currency, o.Status, o.CreatedAt, o.PaidAt))
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
                var sales = db.Orders.AsNoTracking().Where(o => o.Status == OrderStatus.Paid && o.CoachSlug == tenantSlug);

                return TypedResults.Ok(new Page<Sale>(
                    await sales.OrderByDescending(o => o.PaidAt)
                        .Skip((paging.Page - 1) * paging.PageSize).Take(paging.PageSize)
                        .Select(o => new Sale(o.Id, o.ProgramId, o.ProgramTitle, o.CustomerEmail, o.Amount, o.PlatformFee, o.Currency, o.PaidAt!.Value))
                        .ToListAsync(cancellationToken),
                    paging.Page, paging.PageSize, await sales.CountAsync(cancellationToken)));
            })
            .WithName("ListSales")
            .WithTags("Sales");
}
