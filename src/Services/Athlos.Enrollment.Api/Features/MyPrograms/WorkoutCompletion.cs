using Athlos.Enrollment.Api.Data;
using Athlos.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Athlos.Enrollment.Api.Features.MyPrograms;

// PUT and DELETE /api/v1/me/programs/{programId}/workouts/{workoutId}/completion: the customer marks
// a workout of a program they bought as done, or not done. Both are idempotent (204).
// The customer's enrollment is found first (their own rows, MyEnrollments); the write then enters the
// enrollment's tenant, so RLS checks it like any tenant write, and the composite foreign key ties the
// row to that enrollment's tenant and customer.
public static class WorkoutCompletion
{
    public static void MapWorkoutCompletion(this IEndpointRouteBuilder me)
    {
        var completion = me.MapGroup("/programs/{programId:guid}/workouts/{workoutId:guid}/completion").WithTags("My programs");
        completion.MapPut("/", Complete).WithName("CompleteWorkout");
        completion.MapDelete("/", Undo).WithName("UndoWorkoutCompletion");
    }

    private static async Task<Results<NoContent, NotFound>> Complete(
        Guid programId, Guid workoutId, TenantContext tenant, EnrollmentDbContext db, IProgramContent programs, TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (await FindOwnAsync(tenant.UserId, db, programId, cancellationToken) is not { } enrollment)
        {
            return TypedResults.NotFound();
        }

        // Only a workout of this program can be completed.
        var content = await programs.GetAsync(enrollment.TenantId, programId, cancellationToken);
        if (content?.Workouts.Any(w => w.Id == workoutId) != true)
        {
            return TypedResults.NotFound();
        }

        tenant.EnterTenant(enrollment.TenantId, role: null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await db.WorkoutProgress.AnyAsync(p => p.EnrollmentId == enrollment.Id && p.WorkoutId == workoutId, cancellationToken))
        {
            db.WorkoutProgress.Add(new WorkoutProgress
            {
                EnrollmentId = enrollment.Id,
                CustomerId = enrollment.CustomerId,
                WorkoutId = workoutId,
                CompletedAt = time.GetUtcNow(),
            });

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return TypedResults.NoContent(); // A concurrent request completed it first.
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> Undo(
        Guid programId, Guid workoutId, TenantContext tenant, EnrollmentDbContext db, CancellationToken cancellationToken)
    {
        if (await FindOwnAsync(tenant.UserId, db, programId, cancellationToken) is not { } enrollment)
        {
            return TypedResults.NotFound();
        }

        tenant.EnterTenant(enrollment.TenantId, role: null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.WorkoutProgress
            .Where(p => p.EnrollmentId == enrollment.Id && p.CustomerId == enrollment.CustomerId && p.WorkoutId == workoutId)
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<ProgramEnrollment?> FindOwnAsync(Guid? userId, EnrollmentDbContext db, Guid programId, CancellationToken cancellationToken)
    {
        if (userId is not { } customerId)
        {
            return null;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var enrollment = await MyEnrollments.FindAsync(db, customerId, programId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return enrollment;
    }
}
