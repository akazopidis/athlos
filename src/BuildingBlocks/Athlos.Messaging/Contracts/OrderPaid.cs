namespace Athlos.Messaging.Contracts;

// Sent by ordering-api when Stripe confirms a payment (design §2.2), on the order-paid queue.
// enrollment-api creates the customer's enrollment from it, idempotently on OrderId.
// CustomerEmail travels in the message only; it is never logged (design §5.3).
public sealed record OrderPaid(
    Guid OrderId,
    Guid TenantId,
    Guid CustomerId,
    string CustomerEmail,
    Guid ProgramId,
    string ProgramTitle,
    string CoachSlug,
    long Amount,
    string Currency,
    DateTimeOffset PaidAt)
{
    public const string Queue = "order-paid";
}
