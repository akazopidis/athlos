namespace Athlos.Tenancy.Api.Data;

// A report of a program or a coach's storefront to the platform admin (design §2.4, the notice and
// action the EU Digital Services Act asks of a platform). Not tenant data: like the outbox, a work
// queue of the service with an allow-all policy, so the admin lists it without reading any tenant's
// rows. It copies what the admin's list shows. The reporter is only kept to count each person once
// per program or coach; deleting their account blanks it.
public sealed class ContentReport
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required ContentReportKind Kind { get; init; }

    // The workspace that sells the program, or the coach reported.
    public required Guid TenantId { get; init; }

    // Set for a program report.
    public Guid? ProgramId { get; init; }

    // The program's title or the coach's name, as the reporter saw it.
    public required string Subject { get; init; }

    public required string CoachSlug { get; init; }

    public Guid ReporterId { get; set; }

    public required ContentReportReason Reason { get; init; }

    public string? Note { get; init; }

    public DateTimeOffset ReportedAt { get; init; }

    // Closed by the admin's decision, or by a suspension of the coach.
    public DateTimeOffset? ResolvedAt { get; set; }

    public ContentReportOutcome? Outcome { get; set; }
}

public enum ContentReportKind
{
    Program,
    Coach,
}

public enum ContentReportReason
{
    Illegal,
    Misleading,
    Copyright,
    Abusive,
    Other,
}

public enum ContentReportOutcome
{
    // The admin took the program off the marketplace; "Allowed" once they let it back.
    Unpublished,
    Allowed,
    // The admin suspended the coach's workspace.
    Suspended,
    // The admin looked and kept the program or coach as it is.
    Kept,
}
