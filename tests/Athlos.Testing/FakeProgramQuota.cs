using System.Collections.Concurrent;
using Athlos.Catalog.Api.Features.Programs;

namespace Athlos.Testing;

// Stands in for tenancy-api's plan lookup: plenty of programs unless a test sets a limit.
public sealed class FakeProgramQuota : IProgramQuota
{
    private readonly ConcurrentDictionary<Guid, int> _limits = new();

    public void Limit(Guid tenantId, int maxPrograms) => _limits[tenantId] = maxPrograms;

    public Task<int?> MaxProgramsAsync(string tenantSlug, Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<int?>(_limits.GetValueOrDefault(tenantId, 100));
}
