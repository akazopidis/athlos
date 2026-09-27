using Athlos.Tenancy.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Account;

// Called by the SPA after sign-in: keeps user_profiles in step with the Entra token (design §5.2).
// Email and display name come from the token, never from the request body.
public static class UpsertMyProfile
{
    public sealed record Response(string Email, string DisplayName);

    public static void MapUpsertMyProfile(this IEndpointRouteBuilder app) =>
        app.MapPut("/profile", Handle)
            .WithName("UpsertMyProfile")
            .WithTags("Account");

    private static async Task<IResult> Handle(HttpContext http, ITenantContext tenant, TenancyDbContext db, CancellationToken cancellationToken)
    {
        var email = http.User.FindFirst("email")?.Value;
        var name = http.User.FindFirst("name")?.Value;
        if (tenant.UserId is not { } userId || string.IsNullOrWhiteSpace(email))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The token has no email claim.");
        }

        var profile = await db.UserProfiles.SingleOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        if (profile is null)
        {
            profile = new UserProfile { UserId = userId, Email = email, DisplayName = name ?? email };
            db.UserProfiles.Add(profile);
        }
        else
        {
            profile.Email = email;
            profile.DisplayName = name ?? profile.DisplayName;
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new Response(profile.Email, profile.DisplayName));
    }
}
