namespace Athlos.Messaging.Contracts;

// Sent by ordering-api when a paid order is refunded (design §2.2): by its coach in the studio, from
// the Stripe dashboard, or because a dispute was lost. enrollment-api removes the access the order gave,
// inside the order's tenant. Idempotent on OrderId: a second delivery finds nothing to remove.
public sealed record OrderRefunded(Guid OrderId, Guid TenantId, Guid CustomerId, Guid ProgramId, DateTimeOffset RefundedAt)
{
    public const string Queue = "order-refunded";
}
