using Athlos.Tenancy.Data;

namespace Athlos.Ordering.Api.Data;

// A coach's discount code (design §2.4): a percentage off one program or every program of the
// workspace, optionally until a date and for a number of uses. Tenant data with the standard isolation
// policy: checkout looks a code up inside each coach's tenant in the cart, so a code only ever applies
// to the programs of the coach who created it.
public sealed class DiscountCode : ITenantOwned, IHasTimestamps
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    // Unique per workspace; 3 to 20 capital letters, digits or dashes ("SUMMER-20").
    public required string Code { get; init; }

    // 5 to 90.
    public int PercentOff { get; set; }

    // One program of the workspace (catalog's id), or null for all of them.
    public Guid? ProgramId { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    // How many orders may use it (paid, or waiting on an open payment page); null for no limit.
    public int? MaxRedemptions { get; set; }

    public bool IsActive { get; set; } = true;

    // Shown on the coach's programs in the shop ("20% off with SUMMER"), at most one per workspace, and
    // never one with a use limit (design §2.4). catalog-api keeps a copy (PublicDiscounts).
    public bool IsPublic { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
