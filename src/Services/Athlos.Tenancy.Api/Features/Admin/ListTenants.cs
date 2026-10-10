using System.ComponentModel.DataAnnotations;
using Athlos.Tenancy.Api.Data;
using Athlos.Tenancy.Api.Features.Plans;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Admin;

// Every workspace on the platform, for the platform admin (design §1). Tenants are readable by every
// session (RLS tenants_read), so no tenant is entered; memberships and invites stay private.
public static class ListTenants
{
    public const int MaxPageSize = 50;

    public sealed record Item(
        Guid Id, string Slug, string DisplayName, TenantStatus Status, PlanName Plan, bool ReadyToSell, DateTimeOffset CreatedAt);

    // API convention for paged lists (design §7.1).
    public sealed record TenantPage(List<Item> Items, int Page, int PageSize, int TotalCount);

    public sealed record Query(
        string? Q,
        TenantStatus? Status,
        [property: Range(1, 10_000)] int Page = 1,
        [property: Range(1, MaxPageSize)] int PageSize = 20);

    public static void MapListTenants(this IEndpointRouteBuilder app) =>
        app.MapGet("/tenants", Handle)
            .WithName("AdminListTenants");

    private static async Task<Ok<TenantPage>> Handle([AsParameters] Query query, TenancyDbContext db, CancellationToken cancellationToken)
    {
        var tenants = db.Tenants.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var pattern = $"%{EscapeLike(query.Q.Trim())}%";
            tenants = tenants.Where(t => EF.Functions.ILike(t.Slug, pattern, @"\") || EF.Functions.ILike(t.DisplayName, pattern, @"\"));
        }

        if (query.Status is { } status)
        {
            tenants = tenants.Where(t => t.Status == status);
        }

        var total = await tenants.CountAsync(cancellationToken);
        var items = await tenants
            .OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(t => ToItem(t))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new TenantPage(items, query.Page, query.PageSize, total));
    }

    public static Item ToItem(Tenant t) =>
        new(t.Id, t.Slug, t.DisplayName, t.Status, t.Plan, t.StripeOnboardingComplete && t.StripeAccountId != null, t.CreatedAt);

    private static string EscapeLike(string text) =>
        text.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
}
