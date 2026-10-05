namespace Athlos.Tenancy.Api.Features.Plans;

public enum PlanName
{
    Free,
    Pro,
}

// Plan limits drive rate limits in the gateway and quotas in the services (design §4).
// Starting values; the noisy-neighbor experiment in phase 7 may tune them.
public sealed record Plan(PlanName Name, int RequestsPerSecond, int MaxPrograms, int MaxStaff, int MaxMediaGigabytes);

public static class Plans
{
    public static readonly Plan Free = new(PlanName.Free, RequestsPerSecond: 20, MaxPrograms: 5, MaxStaff: 1, MaxMediaGigabytes: 1);
    public static readonly Plan Pro = new(PlanName.Pro, RequestsPerSecond: 100, MaxPrograms: 100, MaxStaff: 10, MaxMediaGigabytes: 20);

    public static IReadOnlyList<Plan> All { get; } = [Free, Pro];

    public static Plan For(PlanName name) => All.Single(p => p.Name == name);
}
