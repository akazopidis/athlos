using System.Collections.Concurrent;
using Athlos.Tenancy.Api.Features.Payouts;

namespace Athlos.Testing;

// Stands in for Stripe Connect (design §15.1: tests use fakes; real Stripe runs only end to end).
public sealed class FakeStripeConnect : IStripeConnect
{
    private readonly ConcurrentDictionary<string, PayoutAccountStatus> _accounts = new();

    public bool IsConfigured => true;

    public int AccountsCreated => _accounts.Count;

    public Task<string> CreateExpressAccountAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var id = $"acct_test_{tenantId:N}";
        _accounts.TryAdd(id, new PayoutAccountStatus(false, false, false));
        return Task.FromResult(id);
    }

    public Task<string> CreateOnboardingLinkAsync(string accountId, Uri returnUrl, Uri refreshUrl, CancellationToken cancellationToken) =>
        Task.FromResult($"https://connect.stripe.test/setup/{accountId}?return={Uri.EscapeDataString(returnUrl.ToString())}");

    public Task<PayoutAccountStatus> GetStatusAsync(string accountId, CancellationToken cancellationToken) =>
        Task.FromResult(_accounts[accountId]);

    // What Stripe reports once the coach has finished onboarding.
    public void CompleteOnboarding(Guid tenantId) => _accounts[$"acct_test_{tenantId:N}"] = new PayoutAccountStatus(true, true, true);
}
