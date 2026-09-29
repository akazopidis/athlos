using Athlos.Tenancy.Data;

namespace Athlos.Ordering.Api.Data;

// A purchase (design §2.3). It belongs to the coach's tenant (their sale) and to the customer (their
// order): RLS lets the tenant see its rows and the customer see theirs, across coaches (design §1).
public sealed class Order : ITenantOwned, IHasTimestamps
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    // Entra object id of the buyer.
    public required Guid CustomerId { get; init; }

    // For the receipt email (design §5.3). Personal data: never logged.
    public required string CustomerEmail { get; init; }

    public required Guid ProgramId { get; init; }

    // Copied at purchase time: a later rename or price change does not rewrite history (design §2.3).
    public required string ProgramTitle { get; init; }

    public required string CoachSlug { get; init; }

    public required long Amount { get; init; }

    public required string Currency { get; init; }

    public required long PlatformFee { get; init; }

    public OrderStatus Status { get; set; } = OrderStatus.Pending;

    // The customer's Idempotency-Key header, unique per customer (design §7.1).
    public string? IdempotencyKey { get; init; }

    public string? StripeCheckoutSessionId { get; set; }

    public string? CheckoutUrl { get; set; }

    public DateTimeOffset? PaidAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum OrderStatus
{
    Pending,
    Paid,
    Failed,
    Refunded,
}

// Stripe retries webhooks; each event is handled once (design §5.1: dedupe on event id).
public sealed class ProcessedStripeEvent
{
    public required string StripeEventId { get; init; }

    public DateTimeOffset ProcessedAt { get; init; } = DateTimeOffset.UtcNow;
}
