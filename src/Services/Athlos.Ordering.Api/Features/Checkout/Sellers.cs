using System.Net;
using System.Net.Http.Json;

namespace Athlos.Ordering.Api.Features.Checkout;

// What checkout needs from the services that own the data (design §2.1: sync calls only where
// needed; ordering never reads their databases). Both calls are signed service-to-service calls.
public interface ISellerDirectory
{
    // A published program's price and seller, or null if it is not for sale.
    Task<ProgramOffer?> GetOfferAsync(Guid programId, CancellationToken cancellationToken);

    // Where the seller's money goes, or null if the workspace is gone.
    Task<PayoutAccount?> GetPayoutAccountAsync(Guid tenantId, CancellationToken cancellationToken);
}

public sealed record ProgramOffer(Guid ProgramId, Guid TenantId, string CoachSlug, string Title, long PriceAmount, string Currency);

public sealed record PayoutAccount(Guid TenantId, bool Active, string? StripeAccountId, bool OnboardingComplete)
{
    public bool CanSell => Active && OnboardingComplete && StripeAccountId is not null;
}

public sealed class SellerDirectory(IHttpClientFactory clients) : ISellerDirectory
{
    public const string Catalog = "catalog-api";
    public const string Tenancy = "tenancy-api";

    public Task<ProgramOffer?> GetOfferAsync(Guid programId, CancellationToken cancellationToken) =>
        GetOrNullAsync<ProgramOffer>(Catalog, $"/internal/programs/{programId}/offer", cancellationToken);

    public Task<PayoutAccount?> GetPayoutAccountAsync(Guid tenantId, CancellationToken cancellationToken) =>
        GetOrNullAsync<PayoutAccount>(Tenancy, $"/internal/tenants/{tenantId}/payout-account", cancellationToken);

    private async Task<T?> GetOrNullAsync<T>(string service, string path, CancellationToken cancellationToken)
        where T : class
    {
        using var response = await clients.CreateClient(service).GetAsync(path, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }
}
