using Athlos.Tenancy.Data;

namespace Athlos.Catalog.Api.Data;

// A coach's program (design §2.3, table "programs"). Named TrainingProgram so it does not clash
// with the app's Program class. Drafts are private to the tenant; published programs are readable
// by everyone through the marketplace (RLS public-read), but only the tenant can change them.
public sealed class TrainingProgram : ITenantOwned, IHasTimestamps
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    // Copy of the owning workspace's slug, for marketplace links (tenant slugs never change).
    // Catalog does not read tenancy's database (design §2.1).
    public required string CoachSlug { get; init; }

    public required string Slug { get; set; }

    public required string Title { get; set; }

    public string Description { get; set; } = "";

    public ProgramLevel Level { get; set; }

    public int DurationWeeks { get; set; }

    // Money is integer minor units plus a currency code (design §2.3).
    public long PriceAmount { get; set; }

    public string Currency { get; set; } = "EUR";

    public bool IsPublished { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    // Set the first time the program is published and never cleared: once a program has been on sale,
    // customers may own it, so it can be unpublished but not deleted (design §3.4).
    public DateTimeOffset? FirstPublishedAt { get; set; }

    // Copy of the workspace's suspension by the platform admin (design §3.4): a suspended coach's
    // programs leave the marketplace and cannot be bought, but customers keep what they bought.
    // Set by tenancy-api through PUT /internal/tenants/{id}/suspension.
    public bool TenantSuspended { get; set; }

    public string? CoverImagePath { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum ProgramLevel
{
    Beginner,
    Intermediate,
    Advanced,
}
