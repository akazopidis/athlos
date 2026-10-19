using System.ComponentModel.DataAnnotations;
using Athlos.Enrollment.Api.Data;
using Athlos.Tenancy;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Enrollment.Api.Features.MyPrograms;

// CROSS-TENANT SLICE (design §3.2, "a customer's own enrollments"): the customer is not a member of
// the coaches' tenants, so these reads ignore the tenant filter and rely on the customer filter
// below and on RLS, which reads enrollments, progress and reviews where customer_id = app.user_id.
// Every customer read of enrollments goes through this file; writes enter the enrollment's tenant.
public static class MyEnrollments
{
    public const int MaxPageSize = 50;

    public sealed record MyProgram(Guid ProgramId, string ProgramTitle, string CoachSlug, DateTimeOffset EnrolledAt, int CompletedWorkouts);

    // API convention for paged lists (design §7.1).
    public sealed record MyProgramsPage(List<MyProgram> Items, int Page, int PageSize, int TotalCount);

    public sealed record Paging([property: Range(1, 10_000)] int Page = 1, [property: Range(1, MaxPageSize)] int PageSize = 20);

    // GET /api/v1/me/programs: what the customer bought, newest first, across coaches.
    public static void MapListMyPrograms(this IEndpointRouteBuilder me) =>
        me.MapGet("/programs", async ([AsParameters] Paging paging, ITenantContext tenant, EnrollmentDbContext db, CancellationToken cancellationToken) =>
            {
                var enrollments = Own(db, tenant.UserId);
                var progress = db.WorkoutProgress.IgnoreQueryFilters([TenantDbContext.TenantFilter]);

                return TypedResults.Ok(new MyProgramsPage(
                    await enrollments.OrderByDescending(e => e.EnrolledAt).ThenBy(e => e.Id)
                        .Skip((paging.Page - 1) * paging.PageSize).Take(paging.PageSize)
                        .Select(e => new MyProgram(e.ProgramId, e.ProgramTitle, e.CoachSlug, e.EnrolledAt,
                            progress.Count(p => p.EnrollmentId == e.Id && p.CustomerId == e.CustomerId)))
                        .ToListAsync(cancellationToken),
                    paging.Page, paging.PageSize, await enrollments.CountAsync(cancellationToken)));
            })
            .WithName("ListMyPrograms")
            .WithTags("My programs");

    // The customer's enrollment in one program, or null: another customer's is invisible (404).
    // Call inside a transaction (RLS settings exist only there).
    public static Task<ProgramEnrollment?> FindAsync(EnrollmentDbContext db, Guid customerId, Guid programId, CancellationToken cancellationToken) =>
        Own(db, customerId).SingleOrDefaultAsync(e => e.ProgramId == programId, cancellationToken);

    // When the customer completed each workout of one of their enrollments.
    public static Task<Dictionary<Guid, DateTimeOffset>> CompletedWorkoutsAsync(EnrollmentDbContext db, ProgramEnrollment enrollment, CancellationToken cancellationToken) =>
        db.WorkoutProgress.AsNoTracking()
            .IgnoreQueryFilters([TenantDbContext.TenantFilter])
            .Where(p => p.EnrollmentId == enrollment.Id && p.CustomerId == enrollment.CustomerId)
            .ToDictionaryAsync(p => p.WorkoutId, p => p.CompletedAt, cancellationToken);

    // The customer's review of one of their enrollments, or null (design §2.4).
    public static Task<Review?> ReviewAsync(EnrollmentDbContext db, ProgramEnrollment enrollment, CancellationToken cancellationToken) =>
        db.Reviews.AsNoTracking()
            .IgnoreQueryFilters([TenantDbContext.TenantFilter])
            .SingleOrDefaultAsync(r => r.EnrollmentId == enrollment.Id && r.CustomerId == enrollment.CustomerId, cancellationToken);

    private static IQueryable<ProgramEnrollment> Own(EnrollmentDbContext db, Guid? customerId) =>
        db.Enrollments.AsNoTracking()
            .IgnoreQueryFilters([TenantDbContext.TenantFilter])
            .Where(e => customerId != null && e.CustomerId == customerId);
}
