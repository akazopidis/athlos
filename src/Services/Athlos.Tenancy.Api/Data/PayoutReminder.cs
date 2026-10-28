namespace Athlos.Tenancy.Api.Data;

// A workspace that started Stripe onboarding (design §5.3): if payouts are still not set up when the
// row is due, three days later, its owners get one reminder. A work queue of the service, like the
// outbox (allow-all policy): it holds the tenant id and a time, none of the tenant's data.
public sealed class PayoutReminder
{
    public required Guid TenantId { get; init; }

    public required DateTimeOffset DueAt { get; init; }
}
