using Athlos.Enrollment.Api.Data;
using Athlos.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Enrollment.Api.Features.Enrollments;

// Deleting a workspace (design §3.4), called by tenancy-api: every enrollment and progress row of one
// tenant. The customers' orders stay in ordering-api for accounting. Not a cross-tenant slice: the
// handler enters that tenant before its transaction, so the tenant filter and RLS limit the deletes
// to its rows. Idempotent.
public static class EraseTenant
{
    public static void MapEraseTenant(this IEndpointRouteBuilder app) =>
        app.MapDelete("/internal/tenants/{tenantId:guid}", Handle)
            .ExcludeFromDescription();

    private static async Task<IResult> Handle(Guid tenantId, TenantContext tenant, EnrollmentDbContext db, CancellationToken cancellationToken)
    {
        tenant.EnterTenant(tenantId, role: null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.WorkoutProgress.ExecuteDeleteAsync(cancellationToken);
        await db.Enrollments.ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
