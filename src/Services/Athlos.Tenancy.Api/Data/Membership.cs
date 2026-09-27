using Athlos.Tenancy.Data;

namespace Athlos.Tenancy.Api.Data;

// Who works for which tenant (design §5: authorization data lives in the app, not the token).
public sealed class Membership : ITenantOwned, IHasTimestamps
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    // Entra object id of the user.
    public required Guid UserId { get; init; }

    public required TenantRole Role { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
