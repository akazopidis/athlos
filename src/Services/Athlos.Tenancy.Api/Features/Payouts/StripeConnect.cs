using Stripe;

namespace Athlos.Tenancy.Api.Features.Payouts;

// Each coach is a Stripe Connect Express account (design §3.2 "Stripe", §5): Stripe collects the
// coach's identity and bank details on its own pages; Athlos stores only the account id.
public interface IStripeConnect
{
    bool IsConfigured { get; }

    Task<string> CreateExpressAccountAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<string> CreateOnboardingLinkAsync(string accountId, Uri returnUrl, Uri refreshUrl, CancellationToken cancellationToken);

    Task<PayoutAccountStatus> GetStatusAsync(string accountId, CancellationToken cancellationToken);
}

public sealed record PayoutAccountStatus(bool DetailsSubmitted, bool ChargesEnabled, bool PayoutsEnabled)
{
    // Ready to be paid through destination charges.
    public bool OnboardingComplete => DetailsSubmitted && ChargesEnabled;
}

public sealed class StripeConnect(IStripeClient stripe, TimeProvider time) : IStripeConnect
{
    public bool IsConfigured => true;

    // Guards against a double click creating two accounts. Stripe keeps the response to a key for 24
    // hours, refusals included, so a fixed key would replay one refusal (found on dev: "not signed up
    // for Connect", long after Connect was set up). The key changes every minute: duplicates within a
    // minute are absorbed, and a refused attempt can be retried a minute later.
    public static string AccountIdempotencyKey(Guid tenantId, DateTimeOffset now) =>
        $"express-account-{tenantId}-{now.UtcDateTime:yyyyMMddHHmm}";

    public async Task<string> CreateExpressAccountAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var account = await new AccountService(stripe).CreateAsync(new AccountCreateOptions
        {
            Type = "express",
            Capabilities = new AccountCapabilitiesOptions
            {
                CardPayments = new AccountCapabilitiesCardPaymentsOptions { Requested = true },
                Transfers = new AccountCapabilitiesTransfersOptions { Requested = true },
            },
            // Lets the Stripe dashboard and webhooks be traced back to the workspace.
            Metadata = new Dictionary<string, string> { ["athlos_tenant_id"] = tenantId.ToString() },
        }, new RequestOptions { IdempotencyKey = AccountIdempotencyKey(tenantId, time.GetUtcNow()) }, cancellationToken);

        return account.Id;
    }

    public async Task<string> CreateOnboardingLinkAsync(string accountId, Uri returnUrl, Uri refreshUrl, CancellationToken cancellationToken)
    {
        var link = await new AccountLinkService(stripe).CreateAsync(new AccountLinkCreateOptions
        {
            Account = accountId,
            Type = "account_onboarding",
            ReturnUrl = returnUrl.ToString(),
            RefreshUrl = refreshUrl.ToString(),
        }, cancellationToken: cancellationToken);

        return link.Url;
    }

    public async Task<PayoutAccountStatus> GetStatusAsync(string accountId, CancellationToken cancellationToken)
    {
        var account = await new AccountService(stripe).GetAsync(accountId, cancellationToken: cancellationToken);
        return new PayoutAccountStatus(account.DetailsSubmitted, account.ChargesEnabled, account.PayoutsEnabled);
    }
}

// Without a Stripe key (for example a fresh local setup) payments are off: endpoints answer 503
// instead of pretending onboarding worked.
public sealed class StripeNotConfigured : IStripeConnect
{
    public bool IsConfigured => false;

    public Task<string> CreateExpressAccountAsync(Guid tenantId, CancellationToken cancellationToken) => throw new InvalidOperationException("Stripe is not configured.");

    public Task<string> CreateOnboardingLinkAsync(string accountId, Uri returnUrl, Uri refreshUrl, CancellationToken cancellationToken) => throw new InvalidOperationException("Stripe is not configured.");

    public Task<PayoutAccountStatus> GetStatusAsync(string accountId, CancellationToken cancellationToken) => throw new InvalidOperationException("Stripe is not configured.");
}

public static class StripeConnectExtensions
{
    // Configuration: Stripe:SecretKey (Key Vault secret stripe-secret-key in Azure, user secrets locally).
    public static IServiceCollection AddStripeConnect(this IServiceCollection services, IConfiguration configuration)
    {
        var secretKey = configuration["Stripe:SecretKey"];
        if (string.IsNullOrWhiteSpace(secretKey))
        {
            services.AddSingleton<IStripeConnect, StripeNotConfigured>();
        }
        else
        {
            services.AddSingleton<IStripeClient>(new StripeClient(secretKey));
            services.AddSingleton<IStripeConnect, StripeConnect>();
        }

        return services;
    }

    public static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult NotConfigured() =>
        TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Payments are not configured for this environment.");
}
