using System.Net.Http.Json;
using Athlos.Ordering.Api.Data;
using Athlos.Ordering.Api.Features.Checkout;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Ordering.Api.Features.Discounts;

// The code a workspace shows on its programs in the shop (design §2.4). Cards and program pages read it
// from catalog-api, which keeps a copy; after every change to the workspace's codes, inside its tenant
// and before the change commits, PublishAsync sends the shown code (or none) to catalog. The values are
// absolute, so a repeated call is harmless; if catalog cannot be reached the change fails (502) and is
// not committed, so the copy does not drift. A shown code has no use limit, so it cannot run out
// between changes; catalog stops showing it when it expires.
public static class PublicDiscounts
{
    public static async Task PublishAsync(OrderingDbContext db, IPublicDiscounts shop, Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var shown = await db.DiscountCodes.AsNoTracking()
            .Where(d => d.IsPublic && d.IsActive && (d.ExpiresAt == null || d.ExpiresAt > now))
            .OrderByDescending(d => d.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        await shop.SetAsync(tenantId, shown, cancellationToken);
    }
}

// catalog-api's PUT and DELETE /internal/tenants/{tenantId}/public-discount (signed).
public interface IPublicDiscounts
{
    Task SetAsync(Guid tenantId, DiscountCode? shown, CancellationToken cancellationToken);
}

public sealed class CatalogPublicDiscounts(IHttpClientFactory clients) : IPublicDiscounts
{
    public async Task SetAsync(Guid tenantId, DiscountCode? shown, CancellationToken cancellationToken)
    {
        var catalog = clients.CreateClient(SellerDirectory.Catalog);
        var path = $"/internal/tenants/{tenantId}/public-discount";
        using var response = shown is null
            ? await catalog.DeleteAsync(path, cancellationToken)
            : await catalog.PutAsJsonAsync(path, new { shown.Code, shown.PercentOff, shown.ProgramId, shown.ExpiresAt }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
