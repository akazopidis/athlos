using Athlos.Enrollment.Api.Data;
using Athlos.Enrollment.Api.Features.Reviews;
using Athlos.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Athlos.Enrollment.Api.Features.MyPrograms;

// GET /api/v1/me/programs/{programId}: a program the customer bought, with its workouts (the paid
// content, from catalog-api), which of them they completed, and their review (design §2.4). Not enrolled: 404, like any resource
// the caller cannot see. Once the coach deleted their workspace, the buyers' copy (CoachLeft, no cover).
public static class GetMyProgram
{
    public sealed record Workout(Guid Id, int Week, int Day, string Title, string Content, string? VideoUrl, DateTimeOffset? CompletedAt);

    public sealed record Response(
        Guid ProgramId, string CoachSlug, string Title, string Description, string Category, int DurationWeeks, string? CoverImageUrl,
        DateTimeOffset EnrolledAt, List<Workout> Workouts, MyReview.Response? Review, bool CoachLeft);

    public static void MapGetMyProgram(this IEndpointRouteBuilder me) =>
        me.MapGet("/programs/{programId:guid}", Handle)
            .WithName("GetMyProgram")
            .WithTags("My programs");

    private static async Task<Results<Ok<Response>, NotFound>> Handle(
        Guid programId, ITenantContext tenant, EnrollmentDbContext db, IProgramContent programs, IServiceScopeFactory scopes, CancellationToken cancellationToken)
    {
        if (tenant.UserId is not { } customerId)
        {
            return TypedResults.NotFound();
        }

        // Read and commit before calling catalog: no database connection is held during the call.
        ProgramEnrollment? enrollment;
        Dictionary<Guid, DateTimeOffset> completed;
        Review? review;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            enrollment = await MyEnrollments.FindAsync(db, customerId, programId, cancellationToken);
            if (enrollment is null)
            {
                return TypedResults.NotFound();
            }

            completed = await MyEnrollments.CompletedWorkoutsAsync(db, enrollment, cancellationToken);
            review = await MyEnrollments.ReviewAsync(db, enrollment, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // Gone with no copy (the coach erased it before buyers kept copies): nothing left to show.
        if (await PaidContent.GetAsync(enrollment, programs, scopes, cancellationToken) is not { } found)
        {
            return TypedResults.NotFound();
        }

        var content = found.Content;

        return TypedResults.Ok(new Response(
            content.ProgramId, content.CoachSlug, content.Title, content.Description, content.Category, content.DurationWeeks, content.CoverImageUrl,
            enrollment.EnrolledAt,
            content.Workouts
                .Select(w => new Workout(w.Id, w.Week, w.Day, w.Title, w.Content, w.VideoUrl, completed.TryGetValue(w.Id, out var at) ? at : null))
                .ToList(),
            review is null ? null : MyReview.ToResponse(review),
            found.CoachLeft));
    }
}
