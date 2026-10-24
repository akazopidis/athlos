using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athlos.Messaging;
using Athlos.Ordering.Api.Data;
using Athlos.Ordering.Api.Features.Checkout;
using Athlos.Ordering.Api.Features.Discounts;

namespace Athlos.Testing;

// catalog-api and tenancy-api, as checkout sees them.
public sealed class FakeSellerDirectory : ISellerDirectory
{
    private readonly ConcurrentDictionary<Guid, ProgramOffer> _offers = new();
    private readonly ConcurrentDictionary<Guid, PayoutAccount> _accounts = new();
    private readonly ConcurrentDictionary<(string CoachSlug, Guid UserId), bool> _members = new();

    // The user is an owner or staff of the coach's workspace.
    public void AddMember(string coachSlug, Guid userId) => _members[(coachSlug, userId)] = true;

    public Task<bool> IsMemberAsync(string coachSlug, Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(_members.ContainsKey((coachSlug, userId)));

    // A published program of a workspace that can take payments.
    public ProgramOffer Sell(Guid tenantId, string coachSlug, long price = 4900, string currency = "EUR")
    {
        var offer = new ProgramOffer(Guid.CreateVersion7(), tenantId, coachSlug, $"Program {Guid.NewGuid():N}"[..20], price, currency);
        _offers[offer.ProgramId] = offer;
        _accounts.TryAdd(tenantId, new PayoutAccount(tenantId, Active: true, $"acct_test_{tenantId:N}", OnboardingComplete: true));
        return offer;
    }

    public void CannotSell(Guid tenantId) => _accounts[tenantId] = new PayoutAccount(tenantId, Active: true, null, OnboardingComplete: false);

    public Task<ProgramOffer?> GetOfferAsync(Guid programId, CancellationToken cancellationToken) =>
        Task.FromResult(_offers.GetValueOrDefault(programId));

    public Task<PayoutAccount?> GetPayoutAccountAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(_accounts.GetValueOrDefault(tenantId));
}

// Stripe Checkout and Transfers: records what would have been sent to Stripe.
public sealed class FakeCheckoutSessions : ICheckoutSessions
{
    private readonly ConcurrentDictionary<Guid, bool> _failingTransfers = new();
    private readonly ConcurrentDictionary<Guid, bool> _failingReversals = new();

    public ConcurrentDictionary<string, CheckoutRequest> Sessions { get; } = new();

    // Order id -> (destination account, amount), one per transfer actually made.
    public ConcurrentDictionary<Guid, (string? Destination, long Amount)> Transfers { get; } = new();

    // Sessions closed by a newer checkout, and sessions the customer already paid at Stripe.
    public ConcurrentDictionary<string, bool> Expired { get; } = new();

    public ConcurrentDictionary<string, bool> PaidAtStripe { get; } = new();

    // Order id -> amount given back, and Stripe's refund reason.
    public ConcurrentDictionary<Guid, (long Amount, string Reason)> Refunds { get; } = new();

    // Orders whose transfer to the coach was taken back.
    public ConcurrentDictionary<Guid, bool> Reversals { get; } = new();

    // Refunds made in the Stripe dashboard: payment intent -> the order each names (null: none).
    public ConcurrentDictionary<string, ConcurrentQueue<Guid?>> DashboardRefunds { get; } = new();

    // The payment intent the fake Stripe gives a session (the same id StripeEvents puts in events).
    public static string PaymentIntentOf(string sessionId) => $"pi_test_{sessionId}";

    public bool IsConfigured => true;

    public static string SessionIdOf(Guid checkoutId) => $"cs_test_{checkoutId:N}";

    // Transfers to this coach fail, as for a restricted Connect account.
    public void FailTransfersTo(Guid tenantId) => _failingTransfers[tenantId] = true;

    // Reversals from this coach fail, as when their Stripe balance cannot cover them.
    public void FailReversalsFrom(Guid tenantId) => _failingReversals[tenantId] = true;

    // A refund made in the Stripe dashboard, naming an order in its metadata or not.
    public void RefundInDashboard(string paymentIntentId, Guid? orderId) =>
        DashboardRefunds.GetOrAdd(paymentIntentId, _ => new ConcurrentQueue<Guid?>()).Enqueue(orderId);

    // Stripe refuses new checkout sessions, as in an outage.
    public bool FailCheckouts { get; set; }

    public Task<CheckoutSession> CreateAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        if (FailCheckouts)
        {
            throw new Stripe.StripeException("Stripe is unavailable.");
        }

        var id = SessionIdOf(request.CheckoutId);
        Sessions[id] = request;
        return Task.FromResult(new CheckoutSession(id, $"https://checkout.stripe.test/{id}"));
    }

    public Task<string> TransferAsync(Order order, string paymentIntentId, CancellationToken cancellationToken)
    {
        if (_failingTransfers.ContainsKey(order.TenantId))
        {
            throw new Stripe.StripeException("Your destination account needs to have at least one of the following capabilities enabled: transfers.");
        }

        Transfers[order.Id] = (order.SellerAccountId, order.Amount - order.PlatformFee);
        return Task.FromResult($"tr_test_{order.Id:N}");
    }

    public Task<bool> ExpireAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (PaidAtStripe.ContainsKey(sessionId))
        {
            return Task.FromResult(false);
        }

        Expired[sessionId] = true;
        return Task.FromResult(true);
    }

    public Task<string> RefundAsync(Order order, string paymentIntentId, string reason, CancellationToken cancellationToken)
    {
        Refunds[order.Id] = (order.Amount, reason);
        return Task.FromResult($"re_test_{order.Id:N}");
    }

    public Task<string> ReverseTransferAsync(Order order, CancellationToken cancellationToken)
    {
        if (_failingReversals.ContainsKey(order.TenantId))
        {
            throw new Stripe.StripeException("Insufficient funds in the destination account to reverse this transfer.");
        }

        Reversals[order.Id] = true;
        return Task.FromResult($"trr_test_{order.Id:N}");
    }

    public Task<(List<(Guid TenantId, Guid OrderId)> Orders, bool Legacy)> OrdersOfPaymentAsync(string paymentIntentId, CancellationToken cancellationToken)
    {
        var session = Sessions.FirstOrDefault(s => PaymentIntentOf(s.Key) == paymentIntentId);
        return Task.FromResult((session.Value?.Orders.Select(o => (o.TenantId, o.Id)).ToList() ?? [], false));
    }

    public Task<string?> PaymentIntentOfSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(PaymentIntentOf(sessionId));

    // Athlos' own refunds of the payment's orders, then the dashboard's.
    public Task<List<Guid?>> RefundsOfPaymentAsync(string paymentIntentId, CancellationToken cancellationToken)
    {
        var session = Sessions.FirstOrDefault(s => PaymentIntentOf(s.Key) == paymentIntentId);
        var own = session.Value?.Orders.Where(o => Refunds.ContainsKey(o.Id)).Select(o => (Guid?)o.Id) ?? [];
        return Task.FromResult(own.Concat(DashboardRefunds.GetValueOrDefault(paymentIntentId) ?? []).ToList());
    }

    // Transfers to this coach work again (the restricted account was fixed).
    public void ResumeTransfersTo(Guid tenantId) => _failingTransfers.TryRemove(tenantId, out _);
}

// Service Bus, as the outbox publisher sees it.
public sealed class CapturingMessageSender : IMessageSender
{
    public ConcurrentQueue<(string Queue, OutboxMessage Message)> Sent { get; } = new();

    public Task SendAsync(string queue, OutboxMessage message, CancellationToken cancellationToken)
    {
        Sent.Enqueue((queue, message));
        return Task.CompletedTask;
    }
}

// Stripe webhook events, signed the way Stripe signs them (t=timestamp,v1=HMAC-SHA256), so the
// real verification code runs in tests.
public static class StripeEvents
{
    public const string SigningSecret = "whsec_test_athlos_0123456789abcdef";

    public static string CheckoutCompleted(string eventId, string sessionId, IEnumerable<(Guid TenantId, Guid OrderId)> orders, string paymentStatus = "paid") =>
        Event(eventId, "checkout.session.completed", sessionId, Metadata(orders), paymentStatus);

    public static string CheckoutExpired(string eventId, string sessionId, IEnumerable<(Guid TenantId, Guid OrderId)> orders) =>
        Event(eventId, "checkout.session.expired", sessionId, Metadata(orders), "unpaid");

    // A full refund made at Stripe (the dashboard), or a partial one.
    public static string ChargeRefunded(string eventId, string paymentIntentId, long amount, long amountRefunded) =>
        JsonSerializer.Serialize(new
        {
            id = eventId,
            @object = "event",
            api_version = "2025-01-01",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            livemode = false,
            type = "charge.refunded",
            data = new
            {
                @object = new
                {
                    id = $"ch_{eventId}",
                    @object = "charge",
                    payment_intent = paymentIntentId,
                    amount,
                    amount_refunded = amountRefunded,
                    refunded = amountRefunded >= amount,
                },
            },
        });

    // A Connect account changed (Connect endpoint: the event names the connected account).
    public static string AccountUpdated(string eventId, string accountId) =>
        JsonSerializer.Serialize(new
        {
            id = eventId,
            @object = "event",
            api_version = "2025-01-01",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            livemode = false,
            type = "account.updated",
            account = accountId,
            data = new { @object = new { id = accountId, @object = "account" } },
        });

    // A dispute the platform lost (or won).
    public static string DisputeClosed(string eventId, string paymentIntentId, string status = "lost") =>
        Dispute(eventId, "charge.dispute.closed", paymentIntentId, status);

    // A customer disputed a payment with their bank; the bank has not decided yet.
    public static string DisputeCreated(string eventId, string paymentIntentId) =>
        Dispute(eventId, "charge.dispute.created", paymentIntentId, "needs_response");

    private static string Dispute(string eventId, string type, string paymentIntentId, string status) =>
        JsonSerializer.Serialize(new
        {
            id = eventId,
            @object = "event",
            api_version = "2025-01-01",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            livemode = false,
            type,
            data = new
            {
                @object = new
                {
                    id = $"dp_{eventId}",
                    @object = "dispute",
                    payment_intent = paymentIntentId,
                    status,
                },
            },
        });

    // A session created before the cart: one order, a destination charge, the old metadata keys.
    public static string LegacyCheckoutCompleted(string eventId, string sessionId, Guid orderId, Guid tenantId) =>
        Event(eventId, "checkout.session.completed", sessionId, new Dictionary<string, string>
        {
            [CheckoutMetadata.OrderId] = orderId.ToString(),
            [CheckoutMetadata.TenantId] = tenantId.ToString(),
        }, "paid");

    private static Dictionary<string, string> Metadata(IEnumerable<(Guid TenantId, Guid OrderId)> orders) =>
        orders.Select((o, i) => (Key: $"{CheckoutMetadata.OrderPrefix}{i}", Value: $"{o.TenantId}:{o.OrderId}"))
            .ToDictionary(p => p.Key, p => p.Value);

    public static string Signature(string payload, DateTimeOffset? at = null, string secret = SigningSecret)
    {
        var timestamp = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
        return $"t={timestamp},v1={Convert.ToHexStringLower(hash)}";
    }

    private static string Event(string eventId, string type, string sessionId, Dictionary<string, string> metadata, string paymentStatus) =>
        JsonSerializer.Serialize(new
        {
            id = eventId,
            @object = "event",
            api_version = "2025-01-01",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            livemode = false,
            type,
            data = new
            {
                @object = new
                {
                    id = sessionId,
                    @object = "checkout.session",
                    payment_status = paymentStatus,
                    payment_intent = $"pi_test_{sessionId}",
                    metadata,
                },
            },
        });
}

// catalog-api's copy of each workspace's shown discount code (design §2.4): the last one sent, or null.
public sealed class FakePublicDiscounts : IPublicDiscounts
{
    public ConcurrentDictionary<Guid, DiscountCode?> Shown { get; } = new();

    // catalog-api cannot be reached: the studio's change must not be kept.
    public bool Unreachable { get; set; }

    public Task SetAsync(Guid tenantId, DiscountCode? shown, CancellationToken cancellationToken)
    {
        if (Unreachable)
        {
            throw new HttpRequestException("catalog-api is unreachable");
        }

        Shown[tenantId] = shown;
        return Task.CompletedTask;
    }
}
