using Athlos.Catalog.Api.Data;
using Athlos.Media;
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

    private static async Task<Ok<List<ProgramDetails>>> Handle(CatalogDbContext db, MediaStore media, CancellationToken cancellationToken)
    {
        var programs = await db.Programs.AsNoTracking()
            .OrderByDescending(p => p.UpdatedAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        var details = new List<ProgramDetails>(programs.Count);
        foreach (var program in programs)
        {
            details.Add(await ProgramDetails.FromAsync(program, media, cancellationToken));
        }

        return TypedResults.Ok(details);
    }
}
