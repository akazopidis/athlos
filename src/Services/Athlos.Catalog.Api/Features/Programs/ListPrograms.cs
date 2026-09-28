using Athlos.Catalog.Api.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Programs;

// All of the current tenant's programs, drafts included. The tenant filter does the scoping.
public static class ListPrograms
{
    public static void MapListPrograms(this IEndpointRouteBuilder app) =>
        app.MapGet("/programs", Handle)
            .WithName("ListPrograms")
            .WithTags("Programs");

    private static async Task<Ok<List<ProgramDetails>>> Handle(CatalogDbContext db, CancellationToken cancellationToken)
    {
        var programs = await db.Programs.AsNoTracking()
            .OrderByDescending(p => p.UpdatedAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(programs.Select(ProgramDetails.From).ToList());
    }
}
