using Athlos.Tenancy.Api.Data;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Account;

// The workspaces the signed-in user belongs to, across tenants.
// CROSS-TENANT SLICE (design §3.2): ignores the tenant filter and relies on the user filter below
// and on the RLS policy, which lets a session read memberships where user_id = app.user_id.
public static class ListMyMemberships
{
    // Status: a suspended workspace is listed, so the studio can say why it is closed (design §3.4).
    public sealed record Item(Guid TenantId, string Slug, string DisplayName, TenantRole Role, TenantStatus Status);

    public static void MapListMyMemberships(this IEndpointRouteBuilder app) =>
        app.MapGet("/memberships", Handle)
            .WithName("ListMyMemberships")
            .WithTags("Account");

    private static async Task<Ok<List<Item>>> Handle(ITenantContext tenant, TenancyDbContext db, CancellationToken cancellationToken)
    {
        var userId = tenant.UserId;
        var items = await (
                from m in db.Memberships.IgnoreQueryFilters([TenantDbContext.TenantFilter])
                where m.UserId == userId
                from t in db.Tenants
                where t.Id == m.TenantId && t.Status != TenantStatus.Deleted
                orderby t.DisplayName
                select new Item(t.Id, t.Slug, t.DisplayName, m.Role, t.Status))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(items);
    }
}
