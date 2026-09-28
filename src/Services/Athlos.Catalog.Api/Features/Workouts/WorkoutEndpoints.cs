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

    private static async Task<Results<Created<WorkoutDetails>, NotFound>> Add(
        string tenantSlug, Guid programId, Request request, CatalogDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Programs.AnyAsync(p => p.Id == programId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var workout = new Workout { ProgramId = programId, Title = request.Title.Trim() };
        Apply(workout, request);
        db.Workouts.Add(workout);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created(
            $"/api/v1/studio/{tenantSlug}/programs/{programId}/workouts/{workout.Id}", WorkoutDetails.From(workout));
    }

    private static async Task<Results<Ok<WorkoutDetails>, NotFound>> Update(
        Guid programId, Guid workoutId, Request request, CatalogDbContext db, CancellationToken cancellationToken)
    {
        var workout = await db.Workouts.SingleOrDefaultAsync(w => w.Id == workoutId && w.ProgramId == programId, cancellationToken);
        if (workout is null)
        {
            return TypedResults.NotFound();
        }

        Apply(workout, request);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(WorkoutDetails.From(workout));
    }

    private static async Task<Results<NoContent, NotFound>> Delete(Guid programId, Guid workoutId, CatalogDbContext db, CancellationToken cancellationToken)
    {
        var workout = await db.Workouts.SingleOrDefaultAsync(w => w.Id == workoutId && w.ProgramId == programId, cancellationToken);
        if (workout is null)
        {
            return TypedResults.NotFound();
        }

        db.Workouts.Remove(workout);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static void Apply(Workout workout, Request request)
    {
        workout.Week = request.Week;
        workout.Day = request.Day;
        workout.Title = request.Title.Trim();
        workout.Content = request.Content ?? "";
        workout.VideoUrl = request.VideoUrl;
    }
}
