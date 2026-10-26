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

    // The coach's discount code this order used, and what it took off the price (design §2.4); Amount
    // is what the customer paid, after it. Null and 0 without a code.
    public string? DiscountCode { get; init; }

    public long DiscountAmount { get; init; }

    public OrderStatus Status { get; set; } = OrderStatus.Pending;

    // The customer's Idempotency-Key header, unique per customer (design §7.1). Set only on orders
    // from before the cart; a cart's key is on its Checkout.
    public string? IdempotencyKey { get; init; }

    // The cart checkout this order was bought in; null for orders from before the cart (design §2.2).
    public Guid? CheckoutId { get; init; }

    // The coach's Stripe Connect account at checkout time: the transfer after payment goes there.
    public string? SellerAccountId { get; init; }

    // The Stripe Transfer of the coach's share, once paid (separate charges and transfers).
    public string? StripeTransferId { get; set; }

    // The customer's payment, set when the order is paid: refunds and disputes name it.
    public string? StripePaymentIntentId { get; set; }

    public DateTimeOffset? RefundedAt { get; set; }

    // The customer asked for their money back, within the refund window (design §2.2), and their
    // optional note to the coach. Personal data: cleared when the customer deletes their account.
    public DateTimeOffset? RefundRequestedAt { get; set; }

    public string? RefundRequestNote { get; set; }

    // One Checkout Session can pay for several orders, of several coaches.
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

// A customer's refund request waiting for the coach (design §2.2): the coach refunds it in Sales, and
// after three working days (DueAt) the platform admin may refund it instead. Not tenant data: like
// payout_retries, a work queue of the service with an allow-all policy, so the admin lists it without
// reading any tenant's orders. It names the order and copies what the list shows; no customer's data.
// Any refund of the order (by the coach, the admin, at Stripe, or a lost dispute) removes the row.
public sealed class RefundRequest
{
    public required Guid OrderId { get; init; }

    public required Guid TenantId { get; init; }

    public required string CoachSlug { get; init; }

    public required string ProgramTitle { get; init; }

    public required long Amount { get; init; }

    public required string Currency { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    public required DateTimeOffset DueAt { get; init; }
}

// A cart checkout (design §2.2): one Stripe payment for programs from any number of coaches. Each
// program is an Order in its coach's tenant; this row is the customer's, not a tenant's, and only
// they can read it (RLS: customer_id = app.user_id). It holds the Idempotency-Key, so retrying a
// checkout returns the same Stripe page.
public sealed class Checkout : IHasTimestamps
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid CustomerId { get; init; }

    public string? IdempotencyKey { get; init; }

    public required string StripeCheckoutSessionId { get; init; }

    public required string CheckoutUrl { get; init; }

    // The version of the Terms the customer accepted for this purchase (design §5.4), and when.
    // Null on checkouts from before v1.12.0.
    public string? TermsVersion { get; init; }

    public DateTimeOffset? TermsAcceptedAt { get; init; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// A coach's share that could not be transferred after payment (for example, a restricted Connect
// account), waiting for PayoutRetries to try again (design §2.2). Not tenant data: like the outbox,
// a work queue of the service, with an allow-all policy. The row only names the order and its tenant;
// the retry enters that tenant to read the order. The admin lists these rows (no cross-tenant read).
public sealed class PayoutRetry
{
    public required Guid OrderId { get; init; }

    public required Guid TenantId { get; init; }

    // Copied for the admin's list, so it needs no read of the tenant's orders.
    public required string CoachSlug { get; init; }

    public required long Amount { get; init; }

    public required string Currency { get; init; }

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

// Stripe retries webhooks; each event is handled once (design §5.1: dedupe on event id).
public sealed class ProcessedStripeEvent
{
    public required string StripeEventId { get; init; }

    public DateTimeOffset ProcessedAt { get; init; } = DateTimeOffset.UtcNow;
}
