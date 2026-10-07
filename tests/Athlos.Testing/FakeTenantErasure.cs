using System.Collections.Concurrent;
using Athlos.Tenancy.Api.Features.Workspace;

namespace Athlos.Testing;

// Stands in for catalog-api and enrollment-api when tenancy-api deletes a workspace: records which
// tenants it was asked to erase, or fails like an unreachable service if a test says so.
public sealed class FakeTenantErasure : ITenantErasure
{
    private readonly ConcurrentDictionary<Guid, bool> _failing = new();

    public ConcurrentBag<Guid> Erased { get; } = [];

    public void FailFor(Guid tenantId) => _failing[tenantId] = true;

    public Task EraseAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (_failing.ContainsKey(tenantId))
        {
            throw new HttpRequestException("catalog-api is not reachable");
        }

        Erased.Add(tenantId);
        return Task.CompletedTask;
    }
}
