using Athlos.Catalog.Api.Data;
using Athlos.Media;
using Athlos.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Programs;

// Publishing puts a program in the marketplace (RLS public-read then applies to it). The workspace
// must be able to sell first: active, with Stripe onboarding finished (design §3.4).
public static class PublishProgram
{
    public static void MapPublishProgram(this IEndpointRouteBuilder app)
    {
        app.MapPost("/programs/{programId:guid}/publish", (Guid programId, CatalogDbContext db, MediaStore media, IPayoutReadiness payouts, ITenantContext tenant, CancellationToken ct) => Publish(programId, db, media, payouts, tenant, ct))
            .WithName("PublishProgram")
            .WithTags("Programs");
        app.MapPost("/programs/{programId:guid}/unpublish", (Guid programId, CatalogDbContext db, MediaStore media, CancellationToken ct) => SetPublished(programId, false, db, media, ct))
            .WithName("UnpublishProgram")
            .WithTags("Programs");
    }

    private static async Task<Results<Ok<ProgramDetails>, NotFound, ProblemHttpResult>> Publish(
        Guid programId, CatalogDbContext db, MediaStore media, IPayoutReadiness payouts, ITenantContext tenant, CancellationToken cancellationToken)
    {
        if (!await db.Programs.AnyAsync(p => p.Id == programId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        if (!await payouts.CanSellAsync(tenant.TenantId!.Value, cancellationToken))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Finish setting up payouts before publishing.");
        }

        return await SetPublished(programId, publish: true, db, media, cancellationToken);
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

        // A program priced before the minimum existed is raised first (it stays on sale meanwhile).
        if (publish && program.PriceAmount < ProgramRules.MinPriceAmount)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
                title: $"Programs cost at least {ProgramRules.MinPriceAmount / 100} {program.Currency}; raise the price before publishing.");
        }

        if (program.IsPublished != publish)
        {
            program.IsPublished = publish;
            program.PublishedAt = publish ? DateTimeOffset.UtcNow : null;
            program.FirstPublishedAt ??= program.PublishedAt;
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(await ProgramDetails.FromAsync(program, media, cancellationToken));
    }
}
