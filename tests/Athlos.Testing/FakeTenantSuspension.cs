using System.Collections.Concurrent;
using Athlos.Tenancy.Api.Features.Admin;

namespace Athlos.Testing;

// Stands in for catalog-api when the platform admin suspends or reactivates a workspace: records the
// last value it was asked to set per tenant, or fails like an unreachable service if a test says so.
public sealed class FakeTenantSuspension : ITenantSuspension
{
    private readonly ConcurrentDictionary<Guid, bool> _failing = new();

    public ConcurrentDictionary<Guid, bool> Suspended { get; } = new();

    public void FailFor(Guid tenantId) => _failing[tenantId] = true;

    public Task SetAsync(Guid tenantId, bool suspended, CancellationToken cancellationToken)
    {
        if (_failing.ContainsKey(tenantId))
        {
            throw new HttpRequestException("catalog-api is not reachable");
        }

        Suspended[tenantId] = suspended;
        return Task.CompletedTask;
    }
}
