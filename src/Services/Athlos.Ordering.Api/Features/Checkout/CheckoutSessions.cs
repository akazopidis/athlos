using Athlos.Ordering.Api.Data;
using Stripe;
using Stripe.Checkout;

namespace Athlos.Ordering.Api.Features.Checkout;

// Stripe Checkout with a destination charge (design §0, §2.2): the customer pays on Stripe's page,
// the coach's Connect account receives the money, and the platform keeps application_fee_amount.
// The order and tenant ids ride along as metadata, so the webhook can find the order under RLS.
public interface ICheckoutSessions
{
    bool IsConfigured { get; }

    Task<CheckoutSession> CreateAsync(Order order, string destinationAccountId, Uri successUrl, Uri cancelUrl, CancellationToken cancellationToken);
}

public sealed record CheckoutSession(string Id, string Url);

public static class CheckoutMetadata
{
    public const string OrderId = "athlos_order_id";
    public const string TenantId = "athlos_tenant_id";
}

public sealed class StripeCheckoutSessions(IStripeClient stripe) : ICheckoutSessions
{
    public bool IsConfigured => true;

    public async Task<CheckoutSession> CreateAsync(Order order, string destinationAccountId, Uri successUrl, Uri cancelUrl, CancellationToken cancellationToken)
    {
        var metadata = new Dictionary<string, string>
        {
            [CheckoutMetadata.OrderId] = order.Id.ToString(),
            [CheckoutMetadata.TenantId] = order.TenantId.ToString(),
        };

        var session = await new SessionService(stripe).CreateAsync(new SessionCreateOptions
        {
            Mode = "payment",
            ClientReferenceId = order.Id.ToString(),
            CustomerEmail = order.CustomerEmail,
            SuccessUrl = successUrl.ToString(),
            CancelUrl = cancelUrl.ToString(),
            Metadata = metadata,
            LineItems =
            [
                new SessionLineItemOptions
                {
                    Quantity = 1,
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = order.Currency.ToLowerInvariant(),
                        UnitAmount = order.Amount,
                        ProductData = new SessionLineItemPriceDataProductDataOptions { Name = order.ProgramTitle },
                    },
                },
            ],
            PaymentIntentData = new SessionPaymentIntentDataOptions
            {
                ApplicationFeeAmount = order.PlatformFee,
                TransferData = new SessionPaymentIntentDataTransferDataOptions { Destination = destinationAccountId },
                Metadata = metadata,
            },
        }, new RequestOptions { IdempotencyKey = $"checkout-{order.Id}" }, cancellationToken);

        return new CheckoutSession(session.Id, session.Url);
    }
}

// Without a Stripe key, checkout answers 503 instead of pretending to take money.
public sealed class CheckoutNotConfigured : ICheckoutSessions
{
    public bool IsConfigured => false;

    public Task<CheckoutSession> CreateAsync(Order order, string destinationAccountId, Uri successUrl, Uri cancelUrl, CancellationToken cancellationToken) =>
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
