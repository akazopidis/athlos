using Athlos.Catalog.Api.Data;
using Athlos.Catalog.Api.Media;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Programs;

// Publishing puts a program in the marketplace (RLS public-read then applies to it).
// Design §3.4 also requires finished Stripe onboarding; that check arrives with Stripe in phase 4.
public static class PublishProgram
{
    public static void MapPublishProgram(this IEndpointRouteBuilder app)
    {
        app.MapPost("/programs/{programId:guid}/publish", (Guid programId, CatalogDbContext db, MediaStore media, CancellationToken ct) => SetPublished(programId, true, db, media, ct))
            .WithName("PublishProgram")
            .WithTags("Programs");
        app.MapPost("/programs/{programId:guid}/unpublish", (Guid programId, CatalogDbContext db, MediaStore media, CancellationToken ct) => SetPublished(programId, false, db, media, ct))
            .WithName("UnpublishProgram")
            .WithTags("Programs");
    }

    private static async Task<Results<Ok<ProgramDetails>, NotFound, ProblemHttpResult>> SetPublished(
        Guid programId, bool publish, CatalogDbContext db, MediaStore media, CancellationToken cancellationToken)
    {
        var program = await db.Programs.SingleOrDefaultAsync(p => p.Id == programId, cancellationToken);
        if (program is null)
        {
            return TypedResults.NotFound();
        }

        if (publish && !await db.Workouts.AnyAsync(w => w.ProgramId == programId, cancellationToken))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Add at least one workout before publishing.");
        }

        if (program.IsPublished != publish)
        {
            program.IsPublished = publish;
            program.PublishedAt = publish ? DateTimeOffset.UtcNow : null;
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(await ProgramDetails.FromAsync(program, media, cancellationToken));
    }
}
