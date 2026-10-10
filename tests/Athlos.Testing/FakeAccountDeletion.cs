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
