using Athlos.Catalog.Api.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Programs;

// Only programs that were never on sale can be deleted: once published, customers may own it and keep
// access even after it is unpublished (design §3.4). Those can be unpublished, not deleted.
public static class DeleteProgram
{
    public static void MapDeleteProgram(this IEndpointRouteBuilder app) =>
        app.MapDelete("/programs/{programId:guid}", Handle)
            .WithName("DeleteProgram")
            .WithTags("Programs");

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> Handle(
        Guid programId, CatalogDbContext db, CancellationToken cancellationToken)
    {
        var program = await db.Programs.SingleOrDefaultAsync(p => p.Id == programId, cancellationToken);
        if (program is null)
        {
            return TypedResults.NotFound();
        }

        if (program.FirstPublishedAt is not null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "This program has been on sale, so customers may own it. Unpublish it instead of deleting it.");
        }

        db.Programs.Remove(program);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
