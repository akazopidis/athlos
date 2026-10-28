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

// catalog's call to enrollment-api after a workout is added to a program that has been on sale.
public sealed class FakeProgramUpdates : Athlos.Catalog.Api.Features.Workouts.IProgramUpdates
{
    public System.Collections.Concurrent.ConcurrentQueue<(Guid TenantId, Guid ProgramId, string WorkoutTitle)> Told { get; } = new();

    public Task WorkoutAddedAsync(Guid tenantId, Guid programId, string workoutTitle, CancellationToken cancellationToken)
    {
        Told.Enqueue((tenantId, programId, workoutTitle));
        return Task.CompletedTask;
    }

    public System.Collections.Concurrent.ConcurrentQueue<Athlos.Catalog.Api.Features.Workouts.PriceDrop> PriceDrops { get; } = new();

    public Task PriceDroppedAsync(Athlos.Catalog.Api.Features.Workouts.PriceDrop drop, CancellationToken cancellationToken)
    {
        PriceDrops.Enqueue(drop);
        return Task.CompletedTask;
    }
}
