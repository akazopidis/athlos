using System.ComponentModel.DataAnnotations;
using Athlos.Catalog.Api.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Workouts;

// A program's workouts, edited by its coach. All four operations share one shape, so they live in
// one slice. The tenant filter makes another tenant's program and workouts invisible (404).
public static class WorkoutEndpoints
{
    public sealed record Request(
        [property: Range(1, 52)] int Week,
        [property: Range(1, 7)] int Day,
        [property: Required, StringLength(120, MinimumLength = 1)] string Title,
        [property: StringLength(20000)] string? Content,
        [property: Url, StringLength(500)] string? VideoUrl);

    public sealed record WorkoutDetails(Guid Id, int Week, int Day, string Title, string Content, string? VideoUrl)
    {
        public static WorkoutDetails From(Workout w) => new(w.Id, w.Week, w.Day, w.Title, w.Content, w.VideoUrl);
    }

    public static void MapWorkouts(this IEndpointRouteBuilder app)
    {
        var workouts = app.MapGroup("/programs/{programId:guid}/workouts").WithTags("Workouts");
        workouts.MapGet("/", List).WithName("ListWorkouts");
        workouts.MapPost("/", Add).WithName("AddWorkout");
        workouts.MapPut("/{workoutId:guid}", Update).WithName("UpdateWorkout");
        workouts.MapDelete("/{workoutId:guid}", Delete).WithName("DeleteWorkout");
    }

    private static async Task<Results<Ok<List<WorkoutDetails>>, NotFound>> List(Guid programId, CatalogDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Programs.AnyAsync(p => p.Id == programId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var workouts = await db.Workouts.AsNoTracking()
            .Where(w => w.ProgramId == programId)
            .OrderBy(w => w.Week).ThenBy(w => w.Day)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(workouts.Select(WorkoutDetails.From).ToList());
    }

    private static async Task<Results<Created<WorkoutDetails>, NotFound, ProblemHttpResult>> Add(
        string tenantSlug, Guid programId, Request request, CatalogDbContext db, CancellationToken cancellationToken)
    {
        var weeks = await db.Programs.Where(p => p.Id == programId).Select(p => (int?)p.DurationWeeks).SingleOrDefaultAsync(cancellationToken);
        if (weeks is null)
        {
            return TypedResults.NotFound();
        }

        if (request.Week > weeks)
        {
            return OutsideTheProgram(weeks.Value);
        }

        var workout = new Workout { ProgramId = programId, Title = request.Title.Trim() };
        Apply(workout, request);
        db.Workouts.Add(workout);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created(
            $"/api/v1/studio/{tenantSlug}/programs/{programId}/workouts/{workout.Id}", WorkoutDetails.From(workout));
    }

    private static async Task<Results<Ok<WorkoutDetails>, NotFound, ProblemHttpResult>> Update(
        Guid programId, Guid workoutId, Request request, CatalogDbContext db, CancellationToken cancellationToken)
    {
        var workout = await db.Workouts.SingleOrDefaultAsync(w => w.Id == workoutId && w.ProgramId == programId, cancellationToken);
        if (workout is null)
        {
            return TypedResults.NotFound();
        }

        var weeks = await db.Programs.Where(p => p.Id == programId).Select(p => p.DurationWeeks).SingleAsync(cancellationToken);
        if (request.Week > weeks)
        {
            return OutsideTheProgram(weeks);
        }

        Apply(workout, request);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(WorkoutDetails.From(workout));
    }

    // A program that has been on sale may have customers, whose progress names its workouts: those
    // workouts can be edited but not deleted (design §3.4, like the program itself).
    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> Delete(Guid programId, Guid workoutId, CatalogDbContext db, CancellationToken cancellationToken)
    {
        var workout = await db.Workouts.SingleOrDefaultAsync(w => w.Id == workoutId && w.ProgramId == programId, cancellationToken);
        if (workout is null)
        {
            return TypedResults.NotFound();
        }

        if (await db.Programs.AnyAsync(p => p.Id == programId && p.FirstPublishedAt != null, cancellationToken))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "This program has been on sale, so customers may be following it: edit the workout instead of deleting it.");
        }

        db.Workouts.Remove(workout);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    // A workout's week is inside the program's length (1 to DurationWeeks).
    private static ProblemHttpResult OutsideTheProgram(int weeks) =>
        TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest,
            title: $"This program lasts {weeks} {(weeks == 1 ? "week" : "weeks")}; pick a week from 1 to {weeks}, or make the program longer first.");

    private static void Apply(Workout workout, Request request)
    {
        workout.Week = request.Week;
        workout.Day = request.Day;
        workout.Title = request.Title.Trim();
        workout.Content = request.Content ?? "";
        workout.VideoUrl = request.VideoUrl;
    }
}
