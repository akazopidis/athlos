using Athlos.Enrollment.Api.Data;
using Athlos.Tenancy;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Enrollment.Api.Features.Enrollments;

// A customer's data export (design §5.4), called by tenancy-api: the programs they own, the workouts
// they completed and their reviews, with every coach.
//
// CROSS-TENANT SLICE (design §3.2, "a customer's own rows, for their export"): acts for that user, so
// the reads ignore the tenant filter and rely on the customer filter below and on RLS
// (customer_id = app.user_id). Read only.
public static class ExportCustomer
{
    public sealed record ExportedReview(int Rating, string? Comment, string AuthorName, DateTimeOffset CreatedAt, string? Reply);

    public sealed record ExportedProgram(
        Guid ProgramId, string ProgramTitle, string CoachSlug, DateTimeOffset EnrolledAt, List<ExportedCompletion> CompletedWorkouts, ExportedReview? Review);

    public sealed record ExportedCompletion(Guid WorkoutId, DateTimeOffset CompletedAt);

    public sealed record Export(List<ExportedProgram> Programs);

    public static void MapExportCustomer(this IEndpointRouteBuilder app) =>
        app.MapGet("/internal/customers/{customerId:guid}/export", Handle)
            .ExcludeFromDescription();

    private static async Task<IResult> Handle(Guid customerId, TenantContext tenant, EnrollmentDbContext db, CancellationToken cancellationToken)
    {
        tenant.ActForUser(customerId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var enrollments = await db.Enrollments.AsNoTracking()
            .IgnoreQueryFilters([TenantDbContext.TenantFilter])
            .Where(e => e.CustomerId == customerId)
            .OrderBy(e => e.EnrolledAt)
            .ToListAsync(cancellationToken);
        var progress = await db.WorkoutProgress.AsNoTracking()
            .IgnoreQueryFilters([TenantDbContext.TenantFilter])
            .Where(p => p.CustomerId == customerId)
            .ToListAsync(cancellationToken);
        var reviews = await db.Reviews.AsNoTracking()
            .IgnoreQueryFilters([TenantDbContext.TenantFilter])
            .Where(r => r.CustomerId == customerId)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new Export([.. enrollments.Select(e => new ExportedProgram(
            e.ProgramId, e.ProgramTitle, e.CoachSlug, e.EnrolledAt,
            [.. progress.Where(p => p.EnrollmentId == e.Id).OrderBy(p => p.CompletedAt).Select(p => new ExportedCompletion(p.WorkoutId, p.CompletedAt))],
            reviews.Where(r => r.EnrollmentId == e.Id).Select(r => new ExportedReview(r.Rating, r.Comment, r.AuthorName, r.CreatedAt, r.Reply)).SingleOrDefault()))]));
    }
}
