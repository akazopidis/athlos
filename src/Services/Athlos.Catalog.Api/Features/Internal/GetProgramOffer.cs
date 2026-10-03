using Athlos.Catalog.Api.Data;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Internal;

// For ordering-api's checkout (design §2.1: "ordering → catalog for price lookup"): what a published
// program costs and which workspace sells it. Drafts are 404, so they cannot be bought.
// CROSS-TENANT SLICE (design §3.2): the buyer is outside the coach's tenant; runs in the public-read
// scope, where RLS only shows published programs.
public static class GetProgramOffer
{
    public sealed record Response(Guid ProgramId, Guid TenantId, string CoachSlug, string Title, long PriceAmount, string Currency);

    public static void MapGetProgramOffer(this IEndpointRouteBuilder app) =>
        app.MapGet("/programs/{programId:guid}/offer", Handle)
            .ExcludeFromDescription();

    private static async Task<Results<Ok<Response>, NotFound>> Handle(Guid programId, CatalogDbContext db, CancellationToken cancellationToken)
    {
        var offer = await db.Programs.AsNoTracking()
            .IgnoreQueryFilters([TenantDbContext.TenantFilter])
            .Where(p => p.Id == programId && p.IsPublished)
            .Select(p => new Response(p.Id, p.TenantId, p.CoachSlug, p.Title, p.PriceAmount, p.Currency))
            .SingleOrDefaultAsync(cancellationToken);

        return offer is null ? TypedResults.NotFound() : TypedResults.Ok(offer);
    }
}
