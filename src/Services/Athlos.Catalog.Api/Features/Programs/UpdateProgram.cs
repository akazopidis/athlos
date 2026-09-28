using System.ComponentModel.DataAnnotations;
using Athlos.Catalog.Api.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Programs;

// Edits apply to published programs too. A price change affects only future purchases, because
// ordering copies the price at purchase time (design §2.3). The slug is fixed once created.
public static class UpdateProgram
{
    public sealed record Request(
        [property: Required, StringLength(120, MinimumLength = 1)] string Title,
        [property: StringLength(4000)] string? Description,
        ProgramLevel Level,
        [property: Range(1, 52)] int DurationWeeks,
        [property: Range(0, ProgramRules.MaxPriceAmount)] long PriceAmount,
        [property: Required, RegularExpression(ProgramRules.CurrencyPattern)] string Currency);

    public static void MapUpdateProgram(this IEndpointRouteBuilder app) =>
        app.MapPut("/programs/{programId:guid}", Handle)
            .WithName("UpdateProgram")
            .WithTags("Programs");

    private static async Task<Results<Ok<ProgramDetails>, NotFound>> Handle(
        Guid programId, Request request, CatalogDbContext db, CancellationToken cancellationToken)
    {
        var program = await db.Programs.SingleOrDefaultAsync(p => p.Id == programId, cancellationToken);
        if (program is null)
        {
            return TypedResults.NotFound();
        }

        program.Title = request.Title.Trim();
        program.Description = request.Description?.Trim() ?? "";
        program.Level = request.Level;
        program.DurationWeeks = request.DurationWeeks;
        program.PriceAmount = request.PriceAmount;
        program.Currency = request.Currency;
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(ProgramDetails.From(program));
    }
}
