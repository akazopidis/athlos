using Athlos.Ordering.Api.Data;
using Athlos.Tenancy;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Ordering.Api.Features.Customers;

// A customer's data export (design §5.4), called by tenancy-api: their orders with every coach and
// their checkouts, with the Terms each accepted.
//
// CROSS-TENANT SLICE (design §3.2, "a customer's own rows, for their export"): acts for that user, so
// the reads ignore the tenant filter and rely on the customer filter below and on RLS
// (customer_id = app.user_id), as the customer's own order list does. Read only.
public static class ExportCustomer
{
    public sealed record ExportedOrder(
        Guid Id, Guid? CheckoutId, string ProgramTitle, string CoachSlug, long Amount, string Currency, string? DiscountCode, long DiscountAmount,
        OrderStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? PaidAt, DateTimeOffset? RefundedAt);

    public sealed record ExportedCheckout(Guid Id, DateTimeOffset CreatedAt, string? TermsVersion, DateTimeOffset? TermsAcceptedAt);

    public sealed record Export(List<ExportedOrder> Orders, List<ExportedCheckout> Checkouts);

    public static void MapExportCustomer(this IEndpointRouteBuilder app) =>
        app.MapGet("/internal/customers/{customerId:guid}/export", Handle)
            .ExcludeFromDescription();

    private static async Task<IResult> Handle(Guid customerId, TenantContext tenant, OrderingDbContext db, CancellationToken cancellationToken)
    {
        tenant.ActForUser(customerId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var orders = await db.Orders.AsNoTracking()
            .IgnoreQueryFilters([TenantDbContext.TenantFilter])
            .Where(o => o.CustomerId == customerId)
            .OrderBy(o => o.CreatedAt)
            .Select(o => new ExportedOrder(o.Id, o.CheckoutId, o.ProgramTitle, o.CoachSlug, o.Amount, o.Currency, o.DiscountCode, o.DiscountAmount,
                o.Status, o.CreatedAt, o.PaidAt, o.RefundedAt))
            .ToListAsync(cancellationToken);
        var checkouts = await db.Checkouts.AsNoTracking()
            .Where(c => c.CustomerId == customerId)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new ExportedCheckout(c.Id, c.CreatedAt, c.TermsVersion, c.TermsAcceptedAt))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new Export(orders, checkouts));
    }
}
