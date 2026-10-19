using Athlos.Tenancy.Data;

namespace Athlos.Enrollment.Api.Data;

// A customer's review of a program they bought (design §2.4). It hangs on their enrollment: one per
// enrollment, and the composite foreign key (enrollment, tenant, customer) cascades, so a refund, a
// deleted account or a deleted workspace takes the review with the access. Belongs to the coach's
// tenant like the enrollment; RLS also lets the customer read their own, and the public-read scope
// read every review (the program page).
public sealed class Review : ITenantOwned, IHasTimestamps
{
    public const int MaxCommentLength = 2000;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public required Guid EnrollmentId { get; init; }

    public required Guid CustomerId { get; init; }

    public required Guid ProgramId { get; init; }

    // 1 to 5 stars.
    public int Rating { get; set; }

    public string? Comment { get; set; }

    // The reviewer's first name and initial ("Maria K."), copied from their token when they write it.
    public required string AuthorName { get; set; }

    // The coach's one public answer.
    public string? Reply { get; set; }

    public DateTimeOffset? RepliedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
