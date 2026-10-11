using Athlos.Ordering.Api.Data;
using Stripe;
using Stripe.Checkout;

namespace Athlos.Ordering.Api.Features.Checkout;

// Stripe Checkout for a cart (design §2.2), with separate charges and transfers: the customer pays the
// platform once for programs from any number of coaches, and after the payment each coach's share
// (price minus the platform fee) goes to their Connect account as a Transfer. A destination charge,
// used before the cart, can pay out to one account only.
// The checkout id and each order's tenant ride along as metadata, so the webhook can find every order
// under RLS, one tenant at a time.
public interface ICheckoutSessions
{
    bool IsConfigured { get; }

    Task<CheckoutSession> CreateAsync(CheckoutRequest request, CancellationToken cancellationToken);

    // Sends a paid order's share to the coach; returns the transfer id. Idempotent per order.
    Task<string> TransferAsync(Order order, string paymentIntentId, CancellationToken cancellationToken);
}

public sealed record CheckoutRequest(Guid CheckoutId, string CustomerEmail, string Currency, IReadOnlyList<Order> Orders, Uri SuccessUrl, Uri CancelUrl);

public sealed record CheckoutSession(string Id, string Url);

public static class CheckoutMetadata
{
    public const string CheckoutId = "athlos_checkout_id";

    // athlos_order_0, athlos_order_1, ...: "<tenant id>:<order id>", one per program.
    public const string OrderPrefix = "athlos_order_";

    // Single-order sessions from before the cart (destination charges, no transfer needed).
    public const string OrderId = "athlos_order_id";
    public const string TenantId = "athlos_tenant_id";

    public static Dictionary<string, string> For(Guid checkoutId, IEnumerable<Order> orders)
    {
        var metadata = new Dictionary<string, string> { [CheckoutId] = checkoutId.ToString() };
        foreach (var (order, i) in orders.Select((o, i) => (o, i)))
        {
            metadata[$"{OrderPrefix}{i}"] = $"{order.TenantId}:{order.Id}";
        }

        return metadata;
    }

    // The orders a session paid for, as (tenant, order) pairs, and whether it is a pre-cart session.
    public static (List<(Guid TenantId, Guid OrderId)> Orders, bool Legacy) Read(IReadOnlyDictionary<string, string>? metadata)
    {
        var orders = new List<(Guid, Guid)>();
        if (metadata is null)
        {
            return (orders, false);
        }

        if (Guid.TryParse(metadata.GetValueOrDefault(TenantId), out var legacyTenant)
            && Guid.TryParse(metadata.GetValueOrDefault(OrderId), out var legacyOrder))
        {
            orders.Add((legacyTenant, legacyOrder));
            return (orders, true);
        }

        foreach (var (key, value) in metadata)
        {
            var parts = value.Split(':');
            if (key.StartsWith(OrderPrefix, StringComparison.Ordinal) && parts.Length == 2
                && Guid.TryParse(parts[0], out var tenant) && Guid.TryParse(parts[1], out var order))
            {
                orders.Add((tenant, order));
            }
        }

        return (orders, false);
    }
}

public sealed class StripeCheckoutSessions(IStripeClient stripe) : ICheckoutSessions
{
    public bool IsConfigured => true;

    public async Task<CheckoutSession> CreateAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        var metadata = CheckoutMetadata.For(request.CheckoutId, request.Orders);

        var session = await new SessionService(stripe).CreateAsync(new SessionCreateOptions
        {
            Mode = "payment",
            ClientReferenceId = request.CheckoutId.ToString(),
            CustomerEmail = request.CustomerEmail,
            SuccessUrl = request.SuccessUrl.ToString(),
            CancelUrl = request.CancelUrl.ToString(),
            Metadata = metadata,
            LineItems = request.Orders.Select(order => new SessionLineItemOptions
            {
                Quantity = 1,
                PriceData = new SessionLineItemPriceDataOptions
                {
                    Currency = request.Currency.ToLowerInvariant(),
                    UnitAmount = order.Amount,
                    ProductData = new SessionLineItemPriceDataProductDataOptions { Name = order.ProgramTitle },
                },
            }).ToList(),
            PaymentIntentData = new SessionPaymentIntentDataOptions
            {
                TransferGroup = request.CheckoutId.ToString(),
                Metadata = metadata,
            },
        }, new RequestOptions { IdempotencyKey = $"checkout-{request.CheckoutId}" }, cancellationToken);

        return new CheckoutSession(session.Id, session.Url);
    }

    public async Task<string> TransferAsync(Order order, string paymentIntentId, CancellationToken cancellationToken)
    {
        // source_transaction ties the transfer to the customer's charge, so it can be made before the
        // charge's funds are available to the platform.
        var payment = await new PaymentIntentService(stripe).GetAsync(paymentIntentId, cancellationToken: cancellationToken);
        var transfer = await new TransferService(stripe).CreateAsync(new TransferCreateOptions
        {
            Amount = order.Amount - order.PlatformFee,
            Currency = order.Currency.ToLowerInvariant(),
            Destination = order.SellerAccountId,
            SourceTransaction = payment.LatestChargeId,
            TransferGroup = order.CheckoutId?.ToString(),
            Metadata = new Dictionary<string, string> { [CheckoutMetadata.OrderId] = order.Id.ToString() },
        }, new RequestOptions { IdempotencyKey = $"transfer-{order.Id}" }, cancellationToken);

        return transfer.Id;
    }
}

// Without a Stripe key, checkout answers 503 instead of pretending to take money.
public sealed class CheckoutNotConfigured : ICheckoutSessions
{
    public bool IsConfigured => false;

    public Task<CheckoutSession> CreateAsync(CheckoutRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Stripe is not configured.");

    public Task<string> TransferAsync(Order order, string paymentIntentId, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Stripe is not configured.");
}

// Verifies Stripe-Signature (HMAC with the endpoint's signing secret) and rejects events older than
// five minutes (design §5.1). Uses Stripe.net's own verification; tests sign with a test secret.
public sealed class StripeWebhookVerifier(string? signingSecret)
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(signingSecret);

    public Event? Verify(string payload, string? signatureHeader)
    {
        if (!IsConfigured || string.IsNullOrEmpty(signatureHeader))
        {
            return null;
        }

        try
        {
            return EventUtility.ConstructEvent(payload, signatureHeader, signingSecret,
                tolerance: (long)Tolerance.TotalSeconds, throwOnApiVersionMismatch: false);
        }
        catch (StripeException)
        {
            return null;
        }
    }
}

public static class PaymentsExtensions
{
    // Configuration: Stripe:SecretKey and Stripe:WebhookSecret (Key Vault in Azure, user secrets
    // locally), Payments:PlatformFeePercent.
    public static IServiceCollection AddPayments(this IServiceCollection services, IConfiguration configuration)
    {
        var secretKey = configuration["Stripe:SecretKey"];
        if (string.IsNullOrWhiteSpace(secretKey))
        {
            services.AddSingleton<ICheckoutSessions, CheckoutNotConfigured>();
        }
        else
        {
            services.AddSingleton<IStripeClient>(new StripeClient(secretKey));
            services.AddSingleton<ICheckoutSessions, StripeCheckoutSessions>();
        }

        services.AddSingleton(new StripeWebhookVerifier(configuration["Stripe:WebhookSecret"]));
        return services;
    }
}
