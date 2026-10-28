namespace Athlos.Enrollment.Api.Data;

// Which of enrollment-api's emails a customer wants (design §5.3, v17.9): the customer's own row, not a
// tenant's, like catalog's saved programs. No row means every email. The senders run inside the coach's
// tenant, where RLS shows the rows of that coach's customers only (tenant_customers_read).
public sealed class EmailPreference
{
    public required Guid CustomerId { get; init; }

    // program-updated: a coach added a workout to a program the customer owns.
    public bool ProgramUpdates { get; set; } = true;

    // review-replied: the coach replied to the customer's review.
    public bool ReviewReplies { get; set; } = true;

    public DateTimeOffset UpdatedAt { get; set; }
}
