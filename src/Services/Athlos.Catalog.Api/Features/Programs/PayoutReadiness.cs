using System.Net;
using System.Net.Http.Json;

namespace Athlos.Catalog.Api.Features.Programs;

// Whether a workspace can sell: it exists, is active, and finished Stripe onboarding (design §3.4).
// tenancy-api owns that data; catalog asks it with a signed call instead of reading its database.
public interface IPayoutReadiness
{
    Task<bool> CanSellAsync(Guid tenantId, CancellationToken cancellationToken);
}

public sealed class TenancyPayoutReadiness(HttpClient tenancy) : IPayoutReadiness
{
    private sealed record PayoutAccount(bool Active, bool OnboardingComplete);

    public async Task<bool> CanSellAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        using var response = await tenancy.GetAsync($"/internal/tenants/{tenantId}/payout-account", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        var account = await response.Content.ReadFromJsonAsync<PayoutAccount>(cancellationToken);
        return account is { Active: true, OnboardingComplete: true };
    }
}
