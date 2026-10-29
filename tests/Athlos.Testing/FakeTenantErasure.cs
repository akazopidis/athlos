using System.Collections.Concurrent;
using Athlos.Tenancy.Api.Features.Workspace;

namespace Athlos.Testing;

// Stands in for catalog-api and enrollment-api when tenancy-api deletes a workspace: records which
// tenants it was asked to erase, or fails like an unreachable service if a test says so.
public sealed class FakeTenantErasure : ITenantErasure
{
    private readonly ConcurrentDictionary<Guid, bool> _failing = new();
    private readonly ConcurrentDictionary<Guid, string> _refusals = new();

    public ConcurrentBag<Guid> Erased { get; } = [];

    public void FailFor(Guid tenantId) => _failing[tenantId] = true;

    // The service is back.
    public void Recover(Guid tenantId) => _failing.TryRemove(tenantId, out _);

    // ordering-api refuses, as when sales can still be refunded.
    public void RefuseFor(Guid tenantId, string reason) => _refusals[tenantId] = reason;

    public Task<string?> PrepareAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(_refusals.GetValueOrDefault(tenantId));

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
