namespace Athlos.Tenancy.Api.Data;

// A workspace deletion that has started and not finished yet (design §3.4, v17.10). A work queue of
// the service, like payout_reminders (allow-all policy): the tenant id, who asked, and the retry state,
// none of the tenant's data. The row goes in the transaction that marks the workspace Deleted.
public sealed class WorkspaceDeletion
{
    public required Guid TenantId { get; init; }

    // The owner, or the platform admin.
    public required Guid RequestedBy { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    public int Attempts { get; set; }

    // When the next attempt may start; an attempt moves it forward first, so it is also a lease.
    public required DateTimeOffset NextAttemptAt { get; set; }

    // Why the last attempt failed, for the admin's list; never personal data.
    public string? LastError { get; set; }
}
