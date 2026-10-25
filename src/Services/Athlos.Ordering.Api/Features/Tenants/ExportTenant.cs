using Athlos.Ordering.Api.Data;
using Athlos.Ordering.Api.Features.Customers;
using Athlos.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Ordering.Api.Features.Tenants;

// A workspace's data export (design §5.4), called by tenancy-api for the owner: its sales, paid or
// refunded, as the studio's sales list shows them (the customer's address is gone once they deleted
// their account), and its discount codes. Not a cross-tenant slice: the handler enters the named
// tenant, so the tenant filter and RLS confine the reads to its rows. Read only.
public static class ExportTenant
{
    public sealed record ExportedSale(
        Guid Id, Guid ProgramId, string ProgramTitle, string? CustomerEmail, long Amount, long PlatformFee, string Currency, string? DiscountCode,
        long DiscountAmount, OrderStatus Status, DateTimeOffset? PaidAt, DateTimeOffset? RefundedAt);

    public sealed record ExportedCode(
        string Code, int PercentOff, Guid? ProgramId, DateTimeOffset? ExpiresAt, int? MaxRedemptions, bool IsActive, bool IsPublic, DateTimeOffset CreatedAt);

    public sealed record Export(List<ExportedSale> Sales, List<ExportedCode> DiscountCodes);

    public static void MapExportTenant(this IEndpointRouteBuilder app) =>
        app.MapGet("/internal/tenants/{tenantId:guid}/export", Handle)
            .ExcludeFromDescription();

    private static async Task<IResult> Handle(Guid tenantId, TenantContext tenant, OrderingDbContext db, CancellationToken cancellationToken)
    {
        tenant.EnterTenant(tenantId, role: null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var sales = await db.Orders.AsNoTracking()
            .Where(o => o.Status == OrderStatus.Paid || o.Status == OrderStatus.Refunded)
            .OrderBy(o => o.PaidAt)
            .Select(o => new ExportedSale(o.Id, o.ProgramId, o.ProgramTitle, o.CustomerEmail == EraseCustomer.ErasedEmail ? null : o.CustomerEmail, o.Amount,
                o.PlatformFee, o.Currency, o.DiscountCode, o.DiscountAmount, o.Status, o.PaidAt, o.RefundedAt))
            .ToListAsync(cancellationToken);
        var codes = await db.DiscountCodes.AsNoTracking()
            .OrderBy(d => d.CreatedAt)
            .Select(d => new ExportedCode(d.Code, d.PercentOff, d.ProgramId, d.ExpiresAt, d.MaxRedemptions, d.IsActive, d.IsPublic, d.CreatedAt))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new Export(sales, codes));
    }
}
