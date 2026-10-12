using Athlos.Enrollment.Api.Data;
using Athlos.Messaging;
using Athlos.Messaging.Contracts;
using Athlos.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Enrollment.Api.Features.Enrollments;

// OrderRefunded from the order-refunded queue (design §2.2): the refunded order's access goes, with
// its progress (the foreign key cascades). The tenant is the one named in the event; the handler enters
// it, so the EF filter and RLS confine the delete to that tenant's rows. Only the enrollment this order
// created is removed: a program the customer still owns through another order keeps its access.
// Idempotent: a redelivered event finds nothing left to remove.
public sealed partial class RevokeOnOrderRefunded(TenantContext tenant, EnrollmentDbContext db, ILogger<RevokeOnOrderRefunded> logger)
    : IMessageHandler<OrderRefunded>
{
    public async Task HandleAsync(OrderRefunded message, CancellationToken cancellationToken)
    {
        tenant.EnterTenant(message.TenantId, role: null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var removed = await db.Enrollments
            .Where(e => e.OrderId == message.OrderId && e.CustomerId == message.CustomerId)
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (removed > 0)
        {
            LogRevoked(logger, message.OrderId, message.ProgramId);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Order {OrderId} was refunded; its access to program {ProgramId} was removed")]
    private static partial void LogRevoked(ILogger logger, Guid orderId, Guid programId);
}
