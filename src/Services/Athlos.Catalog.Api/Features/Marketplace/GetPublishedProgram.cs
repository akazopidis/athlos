using Athlos.Catalog.Api.Data;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Marketplace;

// A program's public page. Drafts are 404 for everyone outside the tenant.
// CROSS-TENANT SLICE (design §3.2), same rules as SearchPrograms. Workouts stay private (paid content).
public static class GetPublishedProgram
{
    public sealed record Response(
        string CoachSlug, string Slug, string Title, string Description, ProgramLevel Level, int DurationWeeks, long PriceAmount, string Currency);

    public static void MapGetPublishedProgram(this IEndpointRouteBuilder app) =>
        app.MapGet("/coaches/{coachSlug}/programs/{programSlug}", Handle)
            .WithName("GetPublishedProgram")
            .WithTags("Marketplace");

    private static async Task<Results<Ok<Response>, NotFound>> Handle(
        string coachSlug, string programSlug, CatalogDbContext db, CancellationToken cancellationToken)
    {
        var program = await db.Programs.AsNoTracking()
            .IgnoreQueryFilters([TenantDbContext.TenantFilter])
            .Where(p => p.IsPublished && p.CoachSlug == coachSlug && p.Slug == programSlug)
            .Select(p => new Response(p.CoachSlug, p.Slug, p.Title, p.Description, p.Level, p.DurationWeeks, p.PriceAmount, p.Currency))
            .SingleOrDefaultAsync(cancellationToken);

        return program is null ? TypedResults.NotFound() : TypedResults.Ok(program);
    }
}
