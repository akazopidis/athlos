using Athlos.Catalog.Api.Data;
using Athlos.Media;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Marketplace;

// A program's public page. Drafts and a suspended coach's programs are 404 for everyone outside the tenant.
// CROSS-TENANT SLICE (design §3.2), same rules as SearchPrograms. Workouts stay private (paid content).
// The id is public: the Buy button sends it to POST /api/v1/orders/checkout.
public static class GetPublishedProgram
{
    public sealed record Response(
        Guid Id, string CoachSlug, string Slug, string Title, string Description, ProgramCategory Category, int DurationWeeks, long PriceAmount, string Currency, string? CoverImageUrl,
        List<OutlineItem> Outline, Preview? Sample);

    // The outline shows every workout's place and title; the free preview is one workout in full.
    public sealed record OutlineItem(int Week, int Day, string Title, bool Sample);

    public sealed record Preview(int Week, int Day, string Title, string Content, string? VideoUrl);

    public static void MapGetPublishedProgram(this IEndpointRouteBuilder app) =>
        app.MapGet("/coaches/{coachSlug}/programs/{programSlug}", Handle)
            .WithName("GetPublishedProgram")
            .WithTags("Marketplace");

    private static async Task<Results<Ok<Response>, NotFound>> Handle(
        string coachSlug, string programSlug, CatalogDbContext db, MediaStore media, CancellationToken cancellationToken)
    {
        var program = await db.Programs.AsNoTracking()
            .IgnoreQueryFilters([TenantDbContext.TenantFilter])
            .Where(p => p.IsPublished && !p.TenantSuspended && p.CoachSlug == coachSlug && p.Slug == programSlug)
            .SingleOrDefaultAsync(cancellationToken);

        return program is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new Response(program.Id, program.CoachSlug, program.Slug, program.Title, program.Description, program.Category,
                program.DurationWeeks, program.PriceAmount, program.Currency, await media.ReadLinkAsync(program.CoverImagePath, cancellationToken),
                [.. program.Outline.Select(o => new OutlineItem(o.Week, o.Day, o.Title, o.Sample))],
                program.Sample is { } s ? new Preview(s.Week, s.Day, s.Title, s.Content, s.VideoUrl) : null));
    }
}
