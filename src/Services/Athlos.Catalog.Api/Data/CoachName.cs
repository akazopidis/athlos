using Athlos.Tenancy.Data;

namespace Athlos.Catalog.Api.Data;

// A workspace's display name, so the shop's search finds programs by their coach's name (design §8,
// v17.9): catalog knows coaches by slug only. A copy: tenancy-api owns the name and sends it through
// PUT /internal/tenants/{id}/coach-name whenever it differs from the copy. Public like the programs
// (RLS public-read for SELECT); only that tenant's call writes it.
public sealed class CoachName : ITenantOwned
{
    // One row per workspace: the key.
    public Guid TenantId { get; set; }

    public required string DisplayName { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
