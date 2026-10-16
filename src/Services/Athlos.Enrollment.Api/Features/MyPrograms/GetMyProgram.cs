using Athlos.Enrollment.Api.Data;
using Athlos.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Athlos.Enrollment.Api.Features.MyPrograms;

// GET /api/v1/me/programs/{programId}: a program the customer bought, with its workouts (the paid
// content, from catalog-api) and which of them they completed. Not enrolled: 404, like any resource
// the caller cannot see.
public static class GetMyProgram
{
    public sealed record Workout(Guid Id, int Week, int Day, string Title, string Content, string? VideoUrl, DateTimeOffset? CompletedAt);

    public sealed record Response(
        Guid ProgramId, string CoachSlug, string Title, string Description, string Category, int DurationWeeks, string? CoverImageUrl,
        DateTimeOffset EnrolledAt, List<Workout> Workouts);

    public static void MapGetMyProgram(this IEndpointRouteBuilder me) =>
        me.MapGet("/programs/{programId:guid}", Handle)
            .WithName("GetMyProgram")
            .WithTags("My programs");

    private static async Task<Results<Ok<Response>, NotFound>> Handle(
        Guid programId, ITenantContext tenant, EnrollmentDbContext db, IProgramContent programs, CancellationToken cancellationToken)
    {
        if (tenant.UserId is not { } customerId)
        {
            return TypedResults.NotFound();
        }

        // Read and commit before calling catalog: no database connection is held during the call.
        ProgramEnrollment? enrollment;
        Dictionary<Guid, DateTimeOffset> completed;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            enrollment = await MyEnrollments.FindAsync(db, customerId, programId, cancellationToken);
            if (enrollment is null)
            {
                return TypedResults.NotFound();
            }

            completed = await MyEnrollments.CompletedWorkoutsAsync(db, enrollment, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // The coach deleted the program: nothing left to show.
        var content = await programs.GetAsync(enrollment.TenantId, programId, cancellationToken);
        if (content is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(new Response(
            content.ProgramId, content.CoachSlug, content.Title, content.Description, content.Category, content.DurationWeeks, content.CoverImageUrl,
            enrollment.EnrolledAt,
            content.Workouts
                .Select(w => new Workout(w.Id, w.Week, w.Day, w.Title, w.Content, w.VideoUrl, completed.TryGetValue(w.Id, out var at) ? at : null))
                .ToList()));
    }
}
