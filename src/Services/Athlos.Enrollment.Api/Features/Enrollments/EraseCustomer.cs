using Athlos.Enrollment.Api.Data;
using Athlos.Enrollment.Api.Features.Reviews;
using Athlos.Tenancy;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Enrollment.Api.Features.Enrollments;

// Deleting a customer's account (design §5.2), called by tenancy-api: their enrollments and progress
// and reviews go, in every coach's tenant, and the ratings of the programs they reviewed follow. (Their orders stay, anonymized, in ordering-api.)
//
// CROSS-TENANT SLICE (design §3.2, "a customer's own rows, for account deletion"): finding the
// tenants ignores the tenant filter and relies on the customer filter below and on RLS
// (customer_id = app.user_id). The deletes then run one tenant at a time, inside that tenant.
// Idempotent.
public static class EraseCustomer
{
    public static void MapEraseCustomer(this IEndpointRouteBuilder app) =>
        app.MapDelete("/internal/customers/{customerId:guid}", Handle)
            .ExcludeFromDescription();

    private static async Task<IResult> Handle(
        Guid customerId, TenantContext tenant, EnrollmentDbContext db, IServiceScopeFactory scopes, IProgramRatings ratings, CancellationToken cancellationToken)
    {
        tenant.ActForUser(customerId);
        List<Guid> tenants;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            tenants = await db.Enrollments.AsNoTracking()
                .IgnoreQueryFilters([TenantDbContext.TenantFilter])
                .Where(e => e.CustomerId == customerId)
                .Select(e => e.TenantId)
                .Distinct()
                .ToListAsync(cancellationToken);
        }

        foreach (var tenantId in tenants)
        {
            await scopes.InTenantAsync<EnrollmentDbContext>(tenantId, async enrollment =>
            {
                var reviewed = await enrollment.Reviews.Where(r => r.CustomerId == customerId).Select(r => r.ProgramId).Distinct().ToListAsync(cancellationToken);
                // In one order, so two erasures never wait on each other's locks.
                reviewed.Sort();
                foreach (var programId in reviewed)
                {
                    await ProgramRatings.LockAsync(enrollment, programId, cancellationToken);
                }

                await enrollment.WorkoutProgress.Where(p => p.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
                await enrollment.Enrollments.Where(e => e.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
                foreach (var programId in reviewed)
                {
                    await ProgramRatings.PublishAsync(enrollment, ratings, tenantId, programId, cancellationToken);
                }
            }, cancellationToken);
        }

        return TypedResults.NoContent();
    }
}
