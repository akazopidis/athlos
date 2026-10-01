using Athlos.Enrollment.Api.Data;
using Athlos.Messaging;
using Athlos.Messaging.Contracts;
using Athlos.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Athlos.Enrollment.Api.Features.Enrollments;

// OrderPaid from the order-paid queue (design §2.2) gives the buyer access to the program.
// The tenant is the one ordering-api wrote into the event (the coach's, from catalog's offer); the
// handler enters it, so RLS checks the insert like any other. Idempotent: a redelivered event, or a
// second paid order for a program the customer already has, finds the unique index and changes nothing.
public sealed partial class EnrollOnOrderPaid(TenantContext tenant, EnrollmentDbContext db, ILogger<EnrollOnOrderPaid> logger)
    : IMessageHandler<OrderPaid>
{
    public async Task HandleAsync(OrderPaid message, CancellationToken cancellationToken)
    {
        tenant.EnterTenant(message.TenantId, role: null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        db.Enrollments.Add(new ProgramEnrollment
        {
            CustomerId = message.CustomerId,
            ProgramId = message.ProgramId,
            OrderId = message.OrderId,
            ProgramTitle = message.ProgramTitle,
            CoachSlug = message.CoachSlug,
            EnrolledAt = message.PaidAt,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } unique)
        {
            // A second paid order for the same program would need a refund; worth seeing in the logs.
            if (unique.ConstraintName?.Contains("program_id", StringComparison.Ordinal) == true)
            {
                LogAlreadyEnrolled(logger, message.OrderId, message.ProgramId);
            }

            return;
        }

        await transaction.CommitAsync(cancellationToken);
        LogEnrolled(logger, message.OrderId, message.ProgramId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Order {OrderId} enrolled its customer in program {ProgramId}")]
    private static partial void LogEnrolled(ILogger logger, Guid orderId, Guid programId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Order {OrderId} is paid, but its customer was already enrolled in program {ProgramId}")]
    private static partial void LogAlreadyEnrolled(ILogger logger, Guid orderId, Guid programId);
}
