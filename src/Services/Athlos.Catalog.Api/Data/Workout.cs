using Athlos.Tenancy.Data;

namespace Athlos.Catalog.Api.Data;

// One session of a program (design §2.3). This is the paid content, so unlike programs it has no
// public-read policy: only the owning tenant reads it until enrollments exist (phase 5).
public sealed class Workout : ITenantOwned, IHasTimestamps
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public required Guid ProgramId { get; init; }

    public int Week { get; set; }

    public int Day { get; set; }

    public required string Title { get; set; }

    // Markdown.
    public string Content { get; set; } = "";

    // A link to an external host (unlisted YouTube, Vimeo); videos are not stored in Azure (design §2.3).
    public string? VideoUrl { get; set; }

    // The program's free preview (design §8): at most one per program, shown on the public program page.
    public bool IsSample { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
