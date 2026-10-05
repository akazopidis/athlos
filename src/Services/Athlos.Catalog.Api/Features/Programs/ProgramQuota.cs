using System.Net;
using System.Net.Http.Json;

namespace Athlos.Catalog.Api.Features.Programs;

// How many programs the workspace's plan allows (design §4, quotas). tenancy-api owns plans; catalog
// asks it with a signed call. Null when the workspace is unknown or not active.
public interface IProgramQuota
{
    Task<int?> MaxProgramsAsync(string tenantSlug, Guid tenantId, CancellationToken cancellationToken);
}

public sealed class TenancyProgramQuota(HttpClient tenancy) : IProgramQuota
{
    private sealed record TenantPlan(Guid TenantId, Plan Plan);

    private sealed record Plan(int MaxPrograms);

    public async Task<int?> MaxProgramsAsync(string tenantSlug, Guid tenantId, CancellationToken cancellationToken)
    {
        using var response = await tenancy.GetAsync($"/internal/tenants/{Uri.EscapeDataString(tenantSlug)}/plan", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var found = await response.Content.ReadFromJsonAsync<TenantPlan>(cancellationToken);
        // The slug comes from the gateway-verified route; it must still name the current tenant.
        return found?.TenantId == tenantId ? found.Plan.MaxPrograms : null;
    }
}
