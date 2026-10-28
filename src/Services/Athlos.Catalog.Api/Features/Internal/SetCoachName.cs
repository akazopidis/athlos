using System.ComponentModel.DataAnnotations;
using Athlos.Catalog.Api.Data;
using Athlos.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Internal;

// For tenancy-api, which owns workspace names: the name the shop's search matches a coach's programs
// by (design §8). An absolute value, so a repeated call is harmless. Not a cross-tenant slice: the
// handler enters the named tenant, so the tenant filter and RLS confine the write to that tenant's row.
public static class SetCoachName
{
    public sealed record Request([property: Required, MaxLength(100)] string DisplayName);

    public static void MapSetCoachName(this IEndpointRouteBuilder app) =>
        app.MapPut("/tenants/{tenantId:guid}/coach-name", Handle).ExcludeFromDescription();

    private static async Task<IResult> Handle(
        Guid tenantId, Request request, TenantContext tenant, CatalogDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        tenant.EnterTenant(tenantId, role: null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO coach_names (tenant_id, display_name, updated_at) VALUES ({tenantId}, {request.DisplayName.Trim()}, {time.GetUtcNow()})
            ON CONFLICT (tenant_id) DO UPDATE SET display_name = excluded.display_name, updated_at = excluded.updated_at
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
