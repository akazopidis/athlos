using System.Collections.Concurrent;
using Athlos.Catalog.Api.Features.Programs;

namespace Athlos.Testing;

// Stands in for tenancy-api's payout lookup: every workspace can sell unless a test says otherwise.
public sealed class FakePayoutReadiness : IPayoutReadiness
{
    private readonly ConcurrentDictionary<Guid, bool> _notReady = new();

    public void NotReady(Guid tenantId) => _notReady[tenantId] = true;

    public Task<bool> CanSellAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(!_notReady.ContainsKey(tenantId));
}
