using System.ComponentModel.DataAnnotations;
using Athlos.Catalog.Api.Data;
using Athlos.Media;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace Athlos.Catalog.Api.Features.Marketplace;

// Browse and search every coach's published programs (design §8, "/"): the shop's catalog, with
// filters (category, coach, price, length), sorting and a coach facet.
// Search (v17.9) matches the words of the title and description (English stemming: "runs" finds
// "running"), a misspelt title ("strenght"), the coach's name or address, or any part of the title or
// description; with a query and no sort chosen, the best matches come first.
// CROSS-TENANT SLICE (design §3.2): ignores the tenant filter, runs in the public-read scope, and
// filters on is_published (and a suspended coach, design §3.4) itself; RLS enforces the same rule, so
// drafts cannot leak even if this filter were wrong.
public static class SearchPrograms
{
    public const int MaxPageSize = 50;

    // Id: what checkout takes, public like on the program page; the shop's "Add to cart" sends it.
    // RatingAverage and RatingCount: the program's reviews (design §2.4); 0 and 0 without any.
    // Discount: the code its coach shows on it, if any (design §2.4).
    public sealed record Item(
        Guid Id,
        string CoachSlug, string Slug, string Title, ProgramCategory Category, int DurationWeeks, long PriceAmount, string Currency, string? CoverImageUrl,
        decimal RatingAverage, int RatingCount, DiscountOffer? Discount);

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

        // Best average first, programs without reviews last; more reviews break a tie.
        TopRated,

        // How well the program matches the query; newest first without one.
        Relevance,
    }

    // Prices in minor units, compared whatever the currency (programs are priced in EUR today).
    // Sort: Relevance with a query, Newest without, unless chosen.
    public sealed record Query(
        string? Q,
        ProgramCategory? Category,
        string? Coach,
        [property: Range(0, long.MaxValue)] long? MinPrice,
        [property: Range(0, long.MaxValue)] long? MaxPrice,
        [property: Range(1, 520)] int? MinWeeks,
        [property: Range(1, 520)] int? MaxWeeks,
        Sort? Sort = null,
        [property: Range(1, 10_000)] int Page = 1,
        [property: Range(1, MaxPageSize)] int PageSize = 20);

    public static void MapSearchPrograms(this IEndpointRouteBuilder app) =>
        app.MapGet("/programs", Handle)
            .WithName("SearchPrograms")
            .WithTags("Marketplace");

    private static async Task<Ok<SearchResults>> Handle(
        [AsParameters] Query query, CatalogDbContext db, MediaStore media, TimeProvider time, CancellationToken cancellationToken)
    {
        var programs = db.Programs.AsNoTracking()
            .IgnoreQueryFilters([TenantDbContext.TenantFilter])
            .Where(p => p.IsPublished && !p.TenantSuspended);

        // The copies of the coaches' names (design §8), public like their programs (RLS public-read).
        var coachNames = db.CoachNames.IgnoreQueryFilters([TenantDbContext.TenantFilter]);
        var text = query.Q?.Trim() ?? "";
        if (text.Length > 0)
        {
            var pattern = $"%{EscapeLike(text)}%";
            // The escape character must be explicit: without it Npgsql emits ESCAPE '' (no escaping).
            // The coach's address too ("forge" finds demo-forge's programs; spaces stand for its dashes).
            var slugPattern = $"%{EscapeLike(text.ToLowerInvariant().Replace(' ', '-'))}%";
            // TrigramsAreWordSimilar is pg_trgm's <%: the query is close to a word or run of words of the
            // title or the coach's name (word similarity at least 0.6), which forgives a typo or two.
            programs = programs.Where(p => EF.Property<NpgsqlTsVector>(p, CatalogDbContext.SearchVector).Matches(EF.Functions.WebSearchToTsQuery("english", text))
                || EF.Functions.TrigramsAreWordSimilar(text, p.Title)
                || EF.Functions.ILike(p.Title, pattern, @"\") || EF.Functions.ILike(p.Description, pattern, @"\")
                || EF.Functions.ILike(p.CoachSlug, slugPattern, @"\")
                || coachNames.Any(c => c.TenantId == p.TenantId
                    && (EF.Functions.TrigramsAreWordSimilar(text, c.DisplayName) || EF.Functions.ILike(c.DisplayName, pattern, @"\"))));
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
        var sorted = (query.Sort ?? (text.Length > 0 ? Sort.Relevance : Sort.Newest)) switch
        {
            // Matching words count most (title above description), then how close the title and the
            // coach's name are to the query; ties go to the newest.
            Sort.Relevance when text.Length > 0 => programs
                .OrderByDescending(p => EF.Property<NpgsqlTsVector>(p, CatalogDbContext.SearchVector).Rank(EF.Functions.WebSearchToTsQuery("english", text))
                    + EF.Functions.TrigramsWordSimilarity(text, p.Title)
                    + (coachNames.Where(c => c.TenantId == p.TenantId).Select(c => (double?)EF.Functions.TrigramsWordSimilarity(text, c.DisplayName)).FirstOrDefault() ?? 0))
                .ThenByDescending(p => p.PublishedAt),
            Sort.PriceLowToHigh => programs.OrderBy(p => p.PriceAmount).ThenByDescending(p => p.PublishedAt),
            Sort.PriceHighToLow => programs.OrderByDescending(p => p.PriceAmount).ThenByDescending(p => p.PublishedAt),
            Sort.TopRated => programs.OrderByDescending(p => p.RatingAverage).ThenByDescending(p => p.RatingCount).ThenByDescending(p => p.PublishedAt),
            _ => programs.OrderByDescending(p => p.PublishedAt),
        };
        var page = await sorted.ThenBy(p => p.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            // The card-size cover where there is one (design §8).
            .Select(p => new { p.Id, p.TenantId, p.CoachSlug, p.Slug, p.Title, p.Category, p.DurationWeeks, p.PriceAmount, p.Currency, CoverImagePath = p.CoverCardPath ?? p.CoverImagePath, p.RatingAverage, p.RatingCount })
            .ToListAsync(cancellationToken);

        // The shown codes of this page's coaches (public like their programs; RLS public-read), unexpired.
        var now = time.GetUtcNow();
        var tenantIds = page.Select(p => p.TenantId).Distinct().ToList();
        var discounts = await db.PublicDiscounts.AsNoTracking()
            .IgnoreQueryFilters([TenantDbContext.TenantFilter])
            .Where(d => tenantIds.Contains(d.TenantId) && (d.ExpiresAt == null || d.ExpiresAt > now))
            .ToDictionaryAsync(d => d.TenantId, cancellationToken);

        var items = new List<Item>(page.Count);
        foreach (var p in page)
        {
            items.Add(new Item(p.Id, p.CoachSlug, p.Slug, p.Title, p.Category, p.DurationWeeks, p.PriceAmount, p.Currency,
                await media.ReadLinkAsync(p.CoverImagePath, cancellationToken), p.RatingAverage, p.RatingCount,
                DiscountOffer.For(p.Id, discounts.GetValueOrDefault(p.TenantId))));
        }

        return TypedResults.Ok(new SearchResults(items, query.Page, query.PageSize, total, coaches));
    }

    // A search for "50%" means the characters, not a wildcard.
    private static string EscapeLike(string text) =>
        text.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
}
