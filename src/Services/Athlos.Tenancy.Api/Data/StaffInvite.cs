using Athlos.Tenancy.Data;

namespace Athlos.Tenancy.Api.Data;

// An owner's invitation for someone to join the workspace as staff (design §5.2). Valid 7 days.
// Only a hash of the token is stored: the token itself exists in the invite email only.
public sealed class StaffInvite : ITenantOwned, IHasTimestamps
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    // Who may accept: the signed-in user's verified email must match. Personal data: never logged.
    public required string Email { get; init; }

    // SHA-256 of the token, hex.
    public required string TokenHash { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? AcceptedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
