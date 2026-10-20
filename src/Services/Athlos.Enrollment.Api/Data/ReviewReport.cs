namespace Athlos.Enrollment.Api.Data;

// Someone's report of a review (design §2.4, moderation), waiting for the platform admin. Not tenant
// data: like ordering's payout_retries, a work queue of the service with an allow-all policy, so the
// admin (who has no tenant) lists reports without reading any tenant's reviews. It copies what the list
// needs; hiding the review enters the review's tenant. One report per review and reporter.
public sealed class ReviewReport
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid ReviewId { get; init; }

    public required Guid TenantId { get; init; }

    public required Guid ProgramId { get; init; }

    // Copies, so the admin reads no tenant's rows.
    public required string ProgramTitle { get; init; }

    public required string CoachSlug { get; init; }

    public required string AuthorName { get; init; }

    public required int Rating { get; init; }

    public string? Comment { get; init; }

    // Entra object id of whoever reported it: the customer, another customer or the coach.
    public required Guid ReporterId { get; init; }

    public required ReportReason Reason { get; init; }

    public string? Note { get; init; }

    public DateTimeOffset ReportedAt { get; init; }

    // Set when the admin decides; the decision is the same for every report of the review.
    public DateTimeOffset? ResolvedAt { get; set; }

    public bool? Hidden { get; set; }
}

public enum ReportReason
{
    Abusive,
    OffTopic,
    PersonalData,
    Spam,
    Other,
}
