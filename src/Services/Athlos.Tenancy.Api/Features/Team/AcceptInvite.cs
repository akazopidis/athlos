using System.ComponentModel.DataAnnotations;
using Athlos.Tenancy.Api.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Team;

// The invitee's side (design §5.2): signed in, they accept and become staff of the workspace.
// The invitee is not a member yet, so the gateway resolves no tenant: the slice enters the workspace
// named in the route itself, like CreateTenant and the membership lookup. That is safe because the
// token decides, not the slug: the invite is looked up inside that workspace by the token's hash,
// and only the user whose verified email matches it can accept. Anything else is 404.
public static class AcceptInvite
{
    public sealed record Request([property: Required, StringLength(100)] string Token);

    public sealed record Response(string Slug, string DisplayName);

    public static void MapAcceptInvite(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/v1/invites/{tenantSlug}/accept", Handle)
            .RequireAuthorization()
            .WithName("AcceptStaffInvite")
            .WithTags("Team");

    private static async Task<Results<Ok<Response>, NotFound, ProblemHttpResult>> Handle(
        string tenantSlug, Request request, HttpContext http, TenantContext tenant, TenancyDbContext db, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var email = http.User.FindFirst("email")?.Value?.Trim().ToLowerInvariant();
        if (tenant.UserId is not { } userId || string.IsNullOrEmpty(email))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized);
        }

        // Tenants are readable by every session (RLS tenants_read).
        var workspace = await db.Tenants.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Slug == tenantSlug && t.Status == TenantStatus.Active, cancellationToken);
        if (workspace is null)
        {
            return TypedResults.NotFound();
        }

        tenant.EnterTenant(workspace.Id, role: null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var hash = StaffInvites.Hash(request.Token);
        var now = time.GetUtcNow();
        var invite = await db.StaffInvites.SingleOrDefaultAsync(i => i.TokenHash == hash && i.AcceptedAt == null, cancellationToken);
        if (invite is null || invite.ExpiresAt <= now || invite.Email != email)
        {
            return TypedResults.NotFound();
        }

        if (!await db.Memberships.AnyAsync(m => m.UserId == userId, cancellationToken))
        {
            db.Memberships.Add(new Membership { UserId = userId, Role = TenantRole.Staff });
        }

        invite.AcceptedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return TypedResults.Ok(new Response(workspace.Slug, workspace.DisplayName));
    }
}
