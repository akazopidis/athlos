using Athlos.Catalog.Api.Data;
using Athlos.Catalog.Api.Media;

namespace Athlos.Catalog.Api.Features.Programs;

// What the studio sees of a program.
public sealed record ProgramDetails(
    Guid Id,
    string Slug,
    string Title,
    string Description,
    ProgramCategory Category,
    int DurationWeeks,
    long PriceAmount,
    string Currency,
    bool IsPublished,
    DateTimeOffset? PublishedAt,
    DateTimeOffset UpdatedAt,
    // A read-only link valid for 15 minutes, or null without a cover.
    string? CoverImageUrl,
    // Has been on sale at some point, so it can no longer be deleted.
    bool EverPublished)
{
    public static async Task<ProgramDetails> FromAsync(TrainingProgram p, MediaStore media, CancellationToken cancellationToken) =>
        new(p.Id, p.Slug, p.Title, p.Description, p.Category, p.DurationWeeks, p.PriceAmount, p.Currency, p.IsPublished, p.PublishedAt, p.UpdatedAt,
            await media.ReadLinkAsync(p.CoverImagePath, cancellationToken), p.FirstPublishedAt is not null);
}

// Fields a coach sets when creating or editing a program. Validation attributes are enforced by
// minimal API validation (design §7).
public static class ProgramRules
{
    public const string SlugPattern = "^[a-z0-9]+(-[a-z0-9]+)*$";

    // ISO 4217; Stripe needs a real currency, and prices are in its minor units.
    public const string CurrencyPattern = "^[A-Z]{3}$";

    // Prices in minor units: at least €10 (no free programs; Stripe also refuses payments under €0.50),
    // at most €10,000.
    public const long MinPriceAmount = 1_000;

    public const long MaxPriceAmount = 1_000_000;
}
