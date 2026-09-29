using System.ComponentModel.DataAnnotations;
using Athlos.Catalog.Api.Data;
using Athlos.Catalog.Api.Media;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Athlos.Catalog.Api.Features.Programs;

// A new program starts as a private draft of the current tenant.
public static class CreateProgram
{
    public sealed record Request(
        [property: Required, RegularExpression(ProgramRules.SlugPattern), StringLength(60, MinimumLength = 3)] string Slug,
        [property: Required, StringLength(120, MinimumLength = 1)] string Title,
        [property: StringLength(4000)] string? Description,
        ProgramLevel Level,
        [property: Range(1, 52)] int DurationWeeks,
        [property: Range(0, ProgramRules.MaxPriceAmount)] long PriceAmount,
        [property: Required, RegularExpression(ProgramRules.CurrencyPattern)] string Currency);

    public static void MapCreateProgram(this IEndpointRouteBuilder app) =>
        app.MapPost("/programs", Handle)
            .WithName("CreateProgram")
            .WithTags("Programs");

    private static async Task<Results<Created<ProgramDetails>, ProblemHttpResult>> Handle(
        string tenantSlug, Request request, CatalogDbContext db, MediaStore media, CancellationToken cancellationToken)
    {
        var program = new TrainingProgram
        {
            CoachSlug = tenantSlug,
            Slug = request.Slug,
            Title = request.Title.Trim(),
            Description = request.Description?.Trim() ?? "",
            Level = request.Level,
            DurationWeeks = request.DurationWeeks,
            PriceAmount = request.PriceAmount,
            Currency = request.Currency,
        };
        db.Programs.Add(program);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "You already have a program with this address.");
        }

        return TypedResults.Created($"/api/v1/studio/{tenantSlug}/programs/{program.Id}", await ProgramDetails.FromAsync(program, media, cancellationToken));
    }
}
