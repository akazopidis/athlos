using Athlos.Catalog.Api.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Programs;

// Another tenant's program is not found, not forbidden (design §3.3).
public static class GetProgram
{
    public static void MapGetProgram(this IEndpointRouteBuilder app) =>
        app.MapGet("/programs/{programId:guid}", Handle)
            .WithName("GetProgram")
            .WithTags("Programs");

    private static async Task<Results<Ok<ProgramDetails>, NotFound>> Handle(Guid programId, CatalogDbContext db, CancellationToken cancellationToken)
    {
        var program = await db.Programs.AsNoTracking().SingleOrDefaultAsync(p => p.Id == programId, cancellationToken);
        return program is null ? TypedResults.NotFound() : TypedResults.Ok(ProgramDetails.From(program));
    }
}
