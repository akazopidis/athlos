using System.Collections.Concurrent;
using Athlos.Tenancy.Api.Features.Payouts;

namespace Athlos.Testing;

// Stands in for Stripe Connect (design §15.1: tests use fakes; real Stripe runs only end to end).
public sealed class FakeStripeConnect : IStripeConnect
{
    private readonly ConcurrentDictionary<string, PayoutAccountStatus> _accounts = new();

    public bool IsConfigured => true;

    public int AccountsCreated => _accounts.Count;

    // Set to make Stripe refuse requests, as it does when Connect is not enabled on the platform.
    public Stripe.StripeException? Failure { get; set; }

    public Task<string> CreateExpressAccountAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        var id = $"acct_test_{tenantId:N}";
        _accounts.TryAdd(id, new PayoutAccountStatus(false, false, false));
        return Task.FromResult(id);
    }

    public Task<string> CreateOnboardingLinkAsync(string accountId, Uri returnUrl, Uri refreshUrl, CancellationToken cancellationToken) =>
        Task.FromResult($"https://connect.stripe.test/setup/{accountId}?return={Uri.EscapeDataString(returnUrl.ToString())}");

    public Task<PayoutAccountStatus> GetStatusAsync(string accountId, CancellationToken cancellationToken) =>
        Task.FromResult(_accounts[accountId]);

    public Task<string> CreateDashboardLinkAsync(string accountId, CancellationToken cancellationToken) =>
        Task.FromResult($"https://connect.stripe.test/express/{accountId}");

    // A balance and two payouts, the newest first, as Stripe lists them.
    public Task<PayoutHistory> GetPayoutHistoryAsync(string accountId, int count, CancellationToken cancellationToken) =>
        Task.FromResult(new PayoutHistory(
            [new Money(4410, "EUR")],
            [new Money(1764, "EUR")],
            [new PayoutToBank(8820, "EUR", new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero), "paid"),
             new PayoutToBank(4410, "EUR", new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), "paid")]));

    // What Stripe reports once the coach has finished onboarding.
    public void CompleteOnboarding(Guid tenantId) => _accounts[$"acct_test_{tenantId:N}"] = new PayoutAccountStatus(true, true, true);

    // Stripe restricted the account (for example, missing verification): it can no longer take charges.
    public void Restrict(Guid tenantId) => _accounts[$"acct_test_{tenantId:N}"] = new PayoutAccountStatus(true, false, false);

    public static string AccountOf(Guid tenantId) => $"acct_test_{tenantId:N}";
}
