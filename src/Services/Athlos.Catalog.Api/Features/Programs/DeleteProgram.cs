using Athlos.Catalog.Api.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Programs;

// Only drafts can be deleted; customers may have bought a published program. Unpublish first.
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

        if (program.IsPublished)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Unpublish the program before deleting it.");
        }

        db.Programs.Remove(program);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
