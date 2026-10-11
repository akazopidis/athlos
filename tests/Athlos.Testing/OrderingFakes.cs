using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athlos.Messaging;
using Athlos.Ordering.Api.Data;
using Athlos.Ordering.Api.Features.Checkout;

namespace Athlos.Testing;

// catalog-api and tenancy-api, as checkout sees them.
public sealed class FakeSellerDirectory : ISellerDirectory
{
    private readonly ConcurrentDictionary<Guid, ProgramOffer> _offers = new();
    private readonly ConcurrentDictionary<Guid, PayoutAccount> _accounts = new();

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

    public ConcurrentDictionary<string, CheckoutRequest> Sessions { get; } = new();

    // Order id -> (destination account, amount), one per transfer actually made.
    public ConcurrentDictionary<Guid, (string? Destination, long Amount)> Transfers { get; } = new();

    public bool IsConfigured => true;

    public static string SessionIdOf(Guid checkoutId) => $"cs_test_{checkoutId:N}";

    // Transfers to this coach fail, as for a restricted Connect account.
    public void FailTransfersTo(Guid tenantId) => _failingTransfers[tenantId] = true;

    public Task<CheckoutSession> CreateAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
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
