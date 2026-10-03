using Athlos.Catalog.Api.Data;
using Athlos.Catalog.Api.Media;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Marketplace;

// A program's public page. Drafts are 404 for everyone outside the tenant.
// CROSS-TENANT SLICE (design §3.2), same rules as SearchPrograms. Workouts stay private (paid content).
// The id is public: the Buy button sends it to POST /api/v1/orders/checkout.
public static class GetPublishedProgram
{
    public sealed record Response(
        Guid Id, string CoachSlug, string Slug, string Title, string Description, ProgramLevel Level, int DurationWeeks, long PriceAmount, string Currency, string? CoverImageUrl);

    public static void MapGetPublishedProgram(this IEndpointRouteBuilder app) =>
        app.MapGet("/coaches/{coachSlug}/programs/{programSlug}", Handle)
            .WithName("GetPublishedProgram")
            .WithTags("Marketplace");

    private static async Task<Results<Ok<Response>, NotFound>> Handle(
        string coachSlug, string programSlug, CatalogDbContext db, MediaStore media, CancellationToken cancellationToken)
    {
        var program = await db.Programs.AsNoTracking()
            .IgnoreQueryFilters([TenantDbContext.TenantFilter])
            .Where(p => p.IsPublished && p.CoachSlug == coachSlug && p.Slug == programSlug)
            .SingleOrDefaultAsync(cancellationToken);

        return program is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new Response(program.Id, program.CoachSlug, program.Slug, program.Title, program.Description, program.Level,
                program.DurationWeeks, program.PriceAmount, program.Currency, await media.ReadLinkAsync(program.CoverImagePath, cancellationToken)));
    }
}
