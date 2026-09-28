using Athlos.Catalog.Api.Data;

namespace Athlos.Catalog.Api.Features.Programs;

// What the studio sees of a program.
public sealed record ProgramDetails(
    Guid Id,
    string Slug,
    string Title,
    string Description,
    ProgramLevel Level,
    int DurationWeeks,
    long PriceAmount,
    string Currency,
    bool IsPublished,
    DateTimeOffset? PublishedAt,
    DateTimeOffset UpdatedAt)
{
    public static ProgramDetails From(TrainingProgram p) =>
        new(p.Id, p.Slug, p.Title, p.Description, p.Level, p.DurationWeeks, p.PriceAmount, p.Currency, p.IsPublished, p.PublishedAt, p.UpdatedAt);
}

// Fields a coach sets when creating or editing a program. Validation attributes are enforced by
// minimal API validation (design §7).
public static class ProgramRules
{
    public const string SlugPattern = "^[a-z0-9]+(-[a-z0-9]+)*$";

    // ISO 4217; Stripe needs a real currency, and prices are in its minor units.
    public const string CurrencyPattern = "^[A-Z]{3}$";

    public const long MaxPriceAmount = 1_000_000;
}
