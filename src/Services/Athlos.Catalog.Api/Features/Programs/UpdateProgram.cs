using System.ComponentModel.DataAnnotations;
using Athlos.Catalog.Api.Data;
using Athlos.Catalog.Api.Features.Workouts;
using Athlos.Media;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Programs;

// Edits apply to published programs too. A price change affects only future purchases, because
// ordering copies the price at purchase time (design §2.3). The slug is fixed once created.
// A lower price on a program in the shop tells the customers who saved it (design §2.4, v17.9): their
// ids are read here, inside the tenant (RLS tenant_savers_read), and enrollment-api is called after the
// response, once the change is committed.
public static class UpdateProgram
{
    public sealed record Request(
        [property: Required, StringLength(120, MinimumLength = 1)] string Title,
        [property: StringLength(4000)] string? Description,
        ProgramCategory Category,
        [property: Range(1, 52)] int DurationWeeks,
        [property: Range(ProgramRules.MinPriceAmount, ProgramRules.MaxPriceAmount)] long PriceAmount,
        [property: Required, RegularExpression(ProgramRules.CurrencyPattern)] string Currency);

    public static void MapUpdateProgram(this IEndpointRouteBuilder app) =>
        app.MapPut("/programs/{programId:guid}", Handle)
            .WithName("UpdateProgram")
            .WithTags("Programs");

    private static async Task<Results<Ok<ProgramDetails>, NotFound, ProblemHttpResult>> Handle(
        Guid programId, Request request, CatalogDbContext db, MediaStore media, IProgramUpdates updates, HttpContext http,
        CancellationToken cancellationToken)
    {
        var program = await db.Programs.SingleOrDefaultAsync(p => p.Id == programId, cancellationToken);
        if (program is null)
        {
            return TypedResults.NotFound();
        }

        // Workouts stay inside the program: it cannot be shortened below its last workout's week.
        var lastWeek = await db.Workouts.Where(w => w.ProgramId == programId).MaxAsync(w => (int?)w.Week, cancellationToken);
        if (lastWeek > request.DurationWeeks)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
                title: $"This program has workouts in week {lastWeek}; move or delete them before making it shorter.");
        }

        var oldPrice = program.PriceAmount;
        program.Title = request.Title.Trim();
        program.Description = request.Description?.Trim() ?? "";
        program.Category = request.Category;
        program.DurationWeeks = request.DurationWeeks;
        program.PriceAmount = request.PriceAmount;
        program.Currency = request.Currency;
        await db.SaveChangesAsync(cancellationToken);

        if (program.PriceAmount < oldPrice && program is { IsPublished: true, TenantSuspended: false, BlockedAt: null })
        {
            var savers = await db.SavedPrograms.AsNoTracking().Where(s => s.ProgramId == programId).Select(s => s.CustomerId).ToListAsync(cancellationToken);
            if (savers.Count > 0)
            {
                var drop = new PriceDrop(program.Id, program.Title, program.CoachSlug, program.Slug, oldPrice, program.PriceAmount, program.Currency, savers);
                http.Response.OnCompleted(() => updates.PriceDroppedAsync(drop, CancellationToken.None));
            }
        }

        return TypedResults.Ok(await ProgramDetails.FromAsync(program, media, cancellationToken));
    }
}
