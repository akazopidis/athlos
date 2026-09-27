using Athlos.Tenancy.Data;

namespace Athlos.Tenancy.Api.Data;

// Created on first sign-in from the token's claims (design §5.2). Owned by the user, not a tenant:
// RLS lets a session see only the row of app.user_id.
public sealed class UserProfile : IHasTimestamps
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid UserId { get; init; }

    // Personal data: never logged.
    public required string Email { get; set; }

    public required string DisplayName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
