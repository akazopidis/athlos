using Athlos.Enrollment.Api.Data;
using Athlos.Enrollment.Api.Features.MyPrograms;
using Athlos.Tenancy;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Enrollment.Api.Features.Enrollments;

// Deleting a workspace (design §3.4), called by tenancy-api before catalog-api erases the programs.
// The customers who bought a program keep it (decided 26 October 2026): for each program they own,
// the content is copied from catalog-api into program_copies, read-only, and their enrollments and
// progress stay. The workspace's reviews and their reports go with the workspace, which no longer
// sells anything. Not a cross-tenant slice: every step enters that tenant, so the tenant filter and
// RLS confine it. Idempotent: a retry after catalog erased the programs keeps the copies it made.
// No database connection is held during the calls to catalog-api.
public static class EraseTenant
{
    public static void MapEraseTenant(this IEndpointRouteBuilder app) =>
        app.MapDelete("/internal/tenants/{tenantId:guid}", Handle)
            .ExcludeFromDescription();

    private static async Task<IResult> Handle(
        Guid tenantId, IServiceScopeFactory scopes, IProgramContent programs, TimeProvider time, CancellationToken cancellationToken)
    {
        List<Guid> bought = [];
        await scopes.InTenantAsync<EnrollmentDbContext>(tenantId, async db =>
            bought = await db.Enrollments.Select(e => e.ProgramId).Distinct().ToListAsync(cancellationToken), cancellationToken);

        foreach (var programId in bought)
        {
            if (await programs.GetAsync(tenantId, programId, cancellationToken) is not { } content)
            {
                continue; // Erased already: an earlier try copied it, or there was nothing left to copy.
            }

            await scopes.InTenantAsync<EnrollmentDbContext>(tenantId, async db =>
            {
                if (await db.ProgramCopies.AnyAsync(c => c.ProgramId == programId, cancellationToken))
                {
                    return;
                }

                db.ProgramCopies.Add(new ProgramCopy
                {
                    ProgramId = programId,
                    CoachSlug = content.CoachSlug,
                    Title = content.Title,
                    Description = content.Description,
                    Category = content.Category,
                    DurationWeeks = content.DurationWeeks,
                    Workouts = PaidContent.WorkoutsJson(content.Workouts),
                    CopiedAt = time.GetUtcNow(),
                });
                await db.SaveChangesAsync(cancellationToken);
            }, cancellationToken);
        }

        await scopes.InTenantAsync<EnrollmentDbContext>(tenantId, async db =>
        {
            await db.Reviews.ExecuteDeleteAsync(cancellationToken);
            // review_reports is the service's work queue, not tenant data: its rows name their tenant.
            await db.ReviewReports.Where(r => r.TenantId == tenantId).ExecuteDeleteAsync(cancellationToken);
        }, cancellationToken);

        return TypedResults.NoContent();
    }
}
