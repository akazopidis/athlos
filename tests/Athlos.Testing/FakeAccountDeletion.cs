using System.Collections.Concurrent;
using Athlos.Tenancy.Api.Features.Account;

namespace Athlos.Testing;

// Stands in for ordering-api and enrollment-api when tenancy-api deletes an account: records whose
// data it was asked to erase, or fails like an unreachable service.
public sealed class FakeCustomerErasure : ICustomerErasure
{
    private readonly ConcurrentDictionary<Guid, bool> _failing = new();

    public ConcurrentBag<Guid> Erased { get; } = [];

    public void FailFor(Guid customerId) => _failing[customerId] = true;

    public Task EraseCustomerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        if (_failing.ContainsKey(customerId))
        {
            throw new HttpRequestException("ordering-api is not reachable");
        }

        Erased.Add(customerId);
        return Task.CompletedTask;
    }
}

// Stands in for Microsoft Graph: configured unless a test says otherwise; records deleted users.
public sealed class FakeEntraUsers : IEntraUsers
{
    private readonly ConcurrentDictionary<Guid, int> _failuresLeft = new();

    public bool Configured { get; set; } = true;

    public ConcurrentBag<Guid> Deleted { get; } = [];

    public void FailOnceFor(Guid userId) => _failuresLeft[userId] = 1;

    public Task DeleteUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (_failuresLeft.TryGetValue(userId, out var left) && left > 0)
        {
            _failuresLeft[userId] = left - 1;
            throw new HttpRequestException("graph.microsoft.com timed out");
        }

        Deleted.Add(userId);
        return Task.CompletedTask;
    }
}

// The other services' export endpoints, as tenancy sees them: fixed parts per customer, or a failure.
public sealed class FakeCustomerExport : ICustomerExport
{
    private readonly ConcurrentDictionary<Guid, bool> _failing = new();

    public void FailFor(Guid customerId) => _failing[customerId] = true;

    public Task<CustomerData> GetAsync(Guid customerId, CancellationToken cancellationToken)
    {
        if (_failing.ContainsKey(customerId))
        {
            throw new HttpRequestException("enrollment-api is not reachable");
        }

        return Task.FromResult(new CustomerData(
            System.Text.Json.JsonSerializer.SerializeToElement(new { orders = new[] { new { programTitle = "Strong start" } } }),
            System.Text.Json.JsonSerializer.SerializeToElement(new { programs = Array.Empty<object>() }),
            System.Text.Json.JsonSerializer.SerializeToElement(new { saved = Array.Empty<object>() })));
    }
}

// The other services' workspace export endpoints, as tenancy sees them: fixed parts, or a failure.
public sealed class FakeTenantExport : Athlos.Tenancy.Api.Features.Workspace.ITenantExport
{
    public bool Failing { get; set; }

    public Task<Athlos.Tenancy.Api.Features.Workspace.TenantData> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (Failing)
        {
            throw new HttpRequestException("ordering-api is not reachable");
        }

        return Task.FromResult(new Athlos.Tenancy.Api.Features.Workspace.TenantData(
            System.Text.Json.JsonSerializer.SerializeToElement(new { programs = new[] { new { title = "Strong start", tenantId } } }),
            System.Text.Json.JsonSerializer.SerializeToElement(new { sales = Array.Empty<object>() }),
            System.Text.Json.JsonSerializer.SerializeToElement(new { reviews = Array.Empty<object>() })));
    }
}
