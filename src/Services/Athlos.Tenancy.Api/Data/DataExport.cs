namespace Athlos.Tenancy.Api.Data;

// A data export someone asked for (design §5.4, v17.10): their account's data or, for an owner, their
// workspace's. A work queue of the service (allow-all policy, like workspace_deletions): ids, times and
// the retry state, never the data itself, which waits in the media container until it expires. The
// endpoints read only the requester's rows.
public sealed class DataExport
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required DataExportKind Kind { get; init; }

    public required Guid RequestedBy { get; init; }

    // The workspace, for a workspace export.
    public Guid? TenantId { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    public int Attempts { get; set; }

    // When the next attempt may start; an attempt moves it forward first, so it is also a lease.
    public required DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? ReadyAt { get; set; }

    // Seven days after it is ready, the file and the row go.
    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? FailedAt { get; set; }

    // Why the last attempt failed; never personal data.
    public string? LastError { get; set; }

    // Under the workspace's prefix for a workspace export, so deleting the workspace removes it with
    // the rest of its media (design §3.4); under the user's otherwise.
    public string BlobPath => Kind == DataExportKind.Workspace ? $"tenants/{TenantId}/exports/{Id}.json" : $"users/{RequestedBy}/exports/{Id}.json";
}

public enum DataExportKind
{
    Account,
    Workspace,
}
