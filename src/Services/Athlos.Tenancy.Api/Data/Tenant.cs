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

// Storefront look, public contact and the coach's profile (design §8). Stored as jsonb.
public sealed class Branding
{
    // A logo linked from elsewhere; an uploaded one (LogoPath) is shown instead when there is one.
    public string? LogoUrl { get; set; }

    // The uploaded logo or photo: a blob path under tenants/{id}/workspace/ (WorkspaceLogo). Never sent
    // to clients; they get a short-lived read link.
    public string? LogoPath { get; set; }

    public string? PrimaryColor { get; set; }

    // The address customers may write to (storefront, "My orders"). Public by the coach's choice; it is
    // not the owners' sign-in addresses, which never leave tenancy-api. Never logged.
    public string? ContactEmail { get; set; }

    // The storefront's "About" (plain text) and links to the coach elsewhere.
    public string? Bio { get; set; }

    public string? InstagramUrl { get; set; }

    public string? YouTubeUrl { get; set; }

    public string? WebsiteUrl { get; set; }

    public Branding With(string? logoPath) => new()
    {
        LogoUrl = LogoUrl, LogoPath = logoPath, PrimaryColor = PrimaryColor, ContactEmail = ContactEmail,
        Bio = Bio, InstagramUrl = InstagramUrl, YouTubeUrl = YouTubeUrl, WebsiteUrl = WebsiteUrl,
    };
}

// The coach's public profile, as storefronts and the studio show it: the logo is the uploaded one's read
// link (15 minutes) or the linked one.
public sealed record CoachProfile(
    string? LogoUrl, string? PrimaryColor, string? ContactEmail, string? Bio, string? InstagramUrl, string? YouTubeUrl, string? WebsiteUrl)
{
    public static async Task<CoachProfile> FromAsync(Branding branding, Athlos.Media.MediaStore media, CancellationToken cancellationToken) => new(
        branding.LogoPath is { } path ? await media.ReadLinkAsync(path, cancellationToken) : branding.LogoUrl,
        branding.PrimaryColor, branding.ContactEmail, branding.Bio, branding.InstagramUrl, branding.YouTubeUrl, branding.WebsiteUrl);
}
