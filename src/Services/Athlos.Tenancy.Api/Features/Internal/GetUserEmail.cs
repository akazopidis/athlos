using Athlos.Tenancy.Api.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Internal;

// For the notifications-worker (signed service-to-service call): where to send an email addressed to
// one user (EmailNotification.ToUser, design §5.3), such as a price drop on a saved program. The
// address is the profile's, kept from the user's sign-in; a user without a profile (or deleted) is 404.
// The handler acts for that user, so RLS (own_profile) shows their profile only.
public static class GetUserEmail
{
    public sealed record Response(Guid UserId, string Email);

    public static void MapGetUserEmail(this IEndpointRouteBuilder app) =>
        app.MapGet("/internal/users/{userId:guid}/email", Handle)
            .ExcludeFromDescription();

    private static async Task<Results<Ok<Response>, NotFound>> Handle(
        Guid userId, TenantContext tenant, TenancyDbContext db, CancellationToken cancellationToken)
    {
        tenant.ActForUser(userId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var email = await db.UserProfiles.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => p.Email)
            .SingleOrDefaultAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return string.IsNullOrWhiteSpace(email) ? TypedResults.NotFound() : TypedResults.Ok(new Response(userId, email));
    }
}
