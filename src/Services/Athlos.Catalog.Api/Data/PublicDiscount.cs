using Athlos.Tenancy.Data;

namespace Athlos.Catalog.Api.Data;

// The discount code a coach shows on their programs (design §2.4), at most one per workspace. A copy:
// ordering-api owns the codes and applies them at checkout, and sends this one through
// PUT /internal/tenants/{id}/public-discount whenever the workspace's codes change. Public like the
// programs it is shown on (RLS public-read for SELECT); only that tenant's call writes it.
public sealed class PublicDiscount : ITenantOwned
{
    // One row per workspace: the key.
    public Guid TenantId { get; set; }

    public required string Code { get; set; }

    public int PercentOff { get; set; }

    // One program of the workspace, or null for all of them.
    public Guid? ProgramId { get; set; }

    // The shop stops showing it then; checkout refuses it then too.
    public DateTimeOffset? ExpiresAt { get; set; }
}
