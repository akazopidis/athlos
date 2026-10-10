using System.ComponentModel.DataAnnotations;
using Athlos.Catalog.Api.Data;
using Athlos.Catalog.Api.Media;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Marketplace;

// Browse and search every coach's published programs (design §8, "/").
// CROSS-TENANT SLICE (design §3.2): ignores the tenant filter, runs in the public-read scope, and
// filters on is_published (and a suspended coach, design §3.4) itself; RLS enforces the same rule, so
// drafts cannot leak even if this filter were wrong.
public static class SearchPrograms
{
    public const int MaxPageSize = 50;

    public sealed record Item(
        string CoachSlug, string Slug, string Title, ProgramLevel Level, int DurationWeeks, long PriceAmount, string Currency, string? CoverImageUrl);

    // API convention for paged lists (design §7.1).
    public sealed record SearchResults(List<Item> Items, int Page, int PageSize, int TotalCount);

    public sealed record Query(
        string? Q,
        ProgramLevel? Level,
        string? Coach,
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

        if (query.Level is { } level)
        {
            programs = programs.Where(p => p.Level == level);
        }

        if (!string.IsNullOrWhiteSpace(query.Coach))
        {
            programs = programs.Where(p => p.CoachSlug == query.Coach);
        }

        var total = await programs.CountAsync(cancellationToken);
        var page = await programs
            .OrderByDescending(p => p.PublishedAt).ThenBy(p => p.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(p => new { p.CoachSlug, p.Slug, p.Title, p.Level, p.DurationWeeks, p.PriceAmount, p.Currency, p.CoverImagePath })
            .ToListAsync(cancellationToken);

        var items = new List<Item>(page.Count);
        foreach (var p in page)
        {
            items.Add(new Item(p.CoachSlug, p.Slug, p.Title, p.Level, p.DurationWeeks, p.PriceAmount, p.Currency,
                await media.ReadLinkAsync(p.CoverImagePath, cancellationToken)));
        }

        return TypedResults.Ok(new SearchResults(items, query.Page, query.PageSize, total));
    }

    // A search for "50%" means the characters, not a wildcard.
    private static string EscapeLike(string text) =>
        text.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
}
