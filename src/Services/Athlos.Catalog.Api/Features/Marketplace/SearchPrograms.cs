using System.ComponentModel.DataAnnotations;
using Athlos.Catalog.Api.Data;
using Athlos.Catalog.Api.Media;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Marketplace;

// Browse and search every coach's published programs (design §8, "/"): the shop's catalog, with
// filters (category, coach, price, length), sorting and a coach facet.
// CROSS-TENANT SLICE (design §3.2): ignores the tenant filter, runs in the public-read scope, and
// filters on is_published (and a suspended coach, design §3.4) itself; RLS enforces the same rule, so
// drafts cannot leak even if this filter were wrong.
public static class SearchPrograms
{
    public const int MaxPageSize = 50;

    // Id: what checkout takes, public like on the program page; the shop's "Add to cart" sends it.
    public sealed record Item(
        Guid Id,
        string CoachSlug, string Slug, string Title, ProgramCategory Category, int DurationWeeks, long PriceAmount, string Currency, string? CoverImageUrl);

    // How many programs each coach has among the results, ignoring the coach filter itself, for the
    // shop's coach filter.
    public sealed record CoachFacet(string CoachSlug, int Count);

    // API convention for paged lists (design §7.1), plus the coach facet.
    public sealed record SearchResults(List<Item> Items, int Page, int PageSize, int TotalCount, List<CoachFacet> Coaches);

    public enum Sort
    {
        Newest,
        PriceLowToHigh,
        PriceHighToLow,
    }

    // Prices in minor units, compared whatever the currency (programs are priced in EUR today).
    public sealed record Query(
        string? Q,
        ProgramCategory? Category,
        string? Coach,
        [property: Range(0, long.MaxValue)] long? MinPrice,
        [property: Range(0, long.MaxValue)] long? MaxPrice,
        [property: Range(1, 520)] int? MinWeeks,
        [property: Range(1, 520)] int? MaxWeeks,
        Sort Sort = Sort.Newest,
        [property: Range(1, 10_000)] int Page = 1,
        [property: Range(1, MaxPageSize)] int PageSize = 20);

    public static void MapSearchPrograms(this IEndpointRouteBuilder app) =>
        app.MapGet("/programs", Handle)
            .WithName("SearchPrograms")
            .WithTags("Marketplace");

    private static async Task<Ok<SearchResults>> Handle([AsParameters] Query query, CatalogDbContext db, MediaStore media, CancellationToken cancellationToken)
    {
        var programs = db.Programs.AsNoTracking()
            .IgnoreQueryFilters([TenantDbContext.TenantFilter])
            .Where(p => p.IsPublished && !p.TenantSuspended);

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var pattern = $"%{EscapeLike(query.Q.Trim())}%";
            // The escape character must be explicit: without it Npgsql emits ESCAPE '' (no escaping).
            programs = programs.Where(p => EF.Functions.ILike(p.Title, pattern, @"\") || EF.Functions.ILike(p.Description, pattern, @"\"));
        }

        if (query.Category is { } category)
        {
            programs = programs.Where(p => p.Category == category);
        }

        if (query.MinPrice is { } minPrice)
        {
            programs = programs.Where(p => p.PriceAmount >= minPrice);
        }

        if (query.MaxPrice is { } maxPrice)
        {
            programs = programs.Where(p => p.PriceAmount <= maxPrice);
        }

        if (query.MinWeeks is { } minWeeks)
        {
            programs = programs.Where(p => p.DurationWeeks >= minWeeks);
        }

        if (query.MaxWeeks is { } maxWeeks)
        {
            programs = programs.Where(p => p.DurationWeeks <= maxWeeks);
        }

        var coaches = await programs
            .GroupBy(p => p.CoachSlug)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Take(MaxPageSize)
            .Select(g => new CoachFacet(g.Key, g.Count()))
            .ToListAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(query.Coach))
        {
            programs = programs.Where(p => p.CoachSlug == query.Coach);
        }

        var total = await programs.CountAsync(cancellationToken);
        var sorted = query.Sort switch
        {
            Sort.PriceLowToHigh => programs.OrderBy(p => p.PriceAmount).ThenByDescending(p => p.PublishedAt),
            Sort.PriceHighToLow => programs.OrderByDescending(p => p.PriceAmount).ThenByDescending(p => p.PublishedAt),
            _ => programs.OrderByDescending(p => p.PublishedAt),
        };
        var page = await sorted.ThenBy(p => p.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(p => new { p.Id, p.CoachSlug, p.Slug, p.Title, p.Category, p.DurationWeeks, p.PriceAmount, p.Currency, p.CoverImagePath })
            .ToListAsync(cancellationToken);

        var items = new List<Item>(page.Count);
        foreach (var p in page)
        {
            items.Add(new Item(p.Id, p.CoachSlug, p.Slug, p.Title, p.Category, p.DurationWeeks, p.PriceAmount, p.Currency,
                await media.ReadLinkAsync(p.CoverImagePath, cancellationToken)));
        }

        return TypedResults.Ok(new SearchResults(items, query.Page, query.PageSize, total, coaches));
    }

    // A search for "50%" means the characters, not a wildcard.
    private static string EscapeLike(string text) =>
        text.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
}
