namespace Athlos.Tenancy.Api.Data;

// An address Communication Services could not deliver to (design §5.3): the notifications-worker
// records it from the delivery report and skips it when it sends. Only a SHA-256 hash of the
// normalized address is kept, never the address. Not tenant data: like the outbox, a list of the
// service's own with an allow-all policy. A row is forgotten after EmailSuppressions.Days.
public sealed class EmailSuppression
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    // Lowercase hex SHA-256 of the trimmed, lowercased address.
    public required string AddressHash { get; init; }

    // The delivery report's status: Bounced or Suppressed.
    public required string Status { get; set; }

    public DateTimeOffset RecordedAt { get; set; }
}
