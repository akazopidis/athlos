using Athlos.Tenancy.Api.Features.Plans;
using Athlos.Tenancy.Data;

namespace Athlos.Tenancy.Api.Data;

// A coach's workspace. Not ITenantOwned: the row *is* the tenant. RLS still lets a session change
// only the tenant it is acting for (see the Initial migration).
public sealed class Tenant : IHasTimestamps
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required string Slug { get; init; }

    public required string DisplayName { get; set; }

    public TenantStatus Status { get; set; } = TenantStatus.Active;

    public PlanName Plan { get; set; } = PlanName.Free;

    public Branding Branding { get; set; } = new();

    public string? StripeAccountId { get; set; }

    public bool StripeOnboardingComplete { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum TenantStatus
{
    Active,
    Suspended,
    Deleted,
}

// Storefront look and public contact (design §8). Stored as jsonb.
public sealed class Branding
{
    public string? LogoUrl { get; set; }

    public string? PrimaryColor { get; set; }

    // The address customers may write to (storefront, "My orders"). Public by the coach's choice; it is
    // not the owners' sign-in addresses, which never leave tenancy-api. Never logged.
    public string? ContactEmail { get; set; }
}
