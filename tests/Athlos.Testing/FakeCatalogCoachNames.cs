using System.Collections.Concurrent;
using Athlos.Tenancy.Api.Features.Storefront;

namespace Athlos.Testing;

// Stands in for catalog-api's copy of the workspaces' names (design §8): what was copied, per tenant.
public sealed class FakeCatalogCoachNames : ICatalogCoachNames
{
    private readonly ConcurrentDictionary<Guid, bool> _failing = new();

    public ConcurrentDictionary<Guid, string> Names { get; } = new();

    public ConcurrentDictionary<Guid, int> Calls { get; } = new();

    public void FailFor(Guid tenantId, bool failing = true)
    {
        if (failing)
        {
            _failing[tenantId] = true;
        }
        else
        {
            _failing.TryRemove(tenantId, out _);
        }
    }

    public Task SetAsync(Guid tenantId, string displayName, CancellationToken cancellationToken)
    {
        Calls.AddOrUpdate(tenantId, 1, (_, n) => n + 1);
        if (_failing.ContainsKey(tenantId))
        {
            throw new HttpRequestException("catalog-api is not reachable");
        }

        Names[tenantId] = displayName;
        return Task.CompletedTask;
    }
}
