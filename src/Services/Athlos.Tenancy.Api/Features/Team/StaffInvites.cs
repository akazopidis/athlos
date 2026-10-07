using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Athlos.Messaging;
using Athlos.Messaging.Contracts;
using Athlos.Tenancy.Api.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Team;

// Coach staff invites, the owner's side (design §5.2): invite by email, see open invites, revoke one.
// The invite email leaves through the outbox in the same transaction as the invite (design §5.3).
// Only the owner manages the team; inside their own workspace staff may learn it exists, so 403.
public static class StaffInvites
{
    public static readonly TimeSpan Validity = TimeSpan.FromDays(7);

    public sealed record Request([property: Required, EmailAddress, StringLength(320)] string Email);

    public sealed record Invite(Guid Id, string Email, DateTimeOffset ExpiresAt, bool Expired);

    public static void MapStaffInvites(this IEndpointRouteBuilder studio)
    {
        var invites = studio.MapGroup("/invites").WithTags("Team");
        invites.MapGet("/", List).WithName("ListStaffInvites");
        invites.MapPost("/", Create).WithName("CreateStaffInvite");
        invites.MapDelete("/{inviteId:guid}", Revoke).WithName("RevokeStaffInvite");
    }

    private static async Task<Results<Ok<List<Invite>>, NotFound, ProblemHttpResult>> List(
        string tenantSlug, ITenantContext tenant, TenancyDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (await Check(tenantSlug, tenant, db, cancellationToken) is { } refused)
        {
            return refused.Result is NotFound ? TypedResults.NotFound() : (ProblemHttpResult)refused.Result;
        }

        var now = time.GetUtcNow();
        var open = await db.StaffInvites.AsNoTracking()
            .Where(i => i.AcceptedAt == null)
            .OrderBy(i => i.CreatedAt)
            .Select(i => new Invite(i.Id, i.Email, i.ExpiresAt, i.ExpiresAt <= now))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(open);
    }

    private static async Task<Results<Created<Invite>, NotFound, ProblemHttpResult>> Create(
        string tenantSlug, Request request, ITenantContext tenant, TenancyDbContext db, TimeProvider time, IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (await Check(tenantSlug, tenant, db, cancellationToken) is { } refused)
        {
            return refused.Result is NotFound ? TypedResults.NotFound() : (ProblemHttpResult)refused.Result;
        }

        var email = request.Email.Trim().ToLowerInvariant();

        // Members' profiles are readable inside the tenant (user_profiles tenant_members_read).
        var alreadyMember = await db.Memberships
            .Join(db.UserProfiles, m => m.UserId, p => p.UserId, (m, p) => p.Email)
            .AnyAsync(e => EF.Functions.ILike(e, EscapeLike(email), @"\"), cancellationToken);
        if (alreadyMember)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "This person is already a member of the workspace.");
        }

        // The plan's staff quota (design §4): staff members plus open invites, each of which holds a
        // place until it is accepted or expires. Inviting the same address again does not count twice.
        var workspace = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenant.TenantId, cancellationToken);
        var now = time.GetUtcNow();
        if (await StaffPlacesTakenAsync(db, now, exceptEmail: email, cancellationToken) >= Plans.Plans.For(workspace.Plan).MaxStaff)
        {
            return StaffQuotaReached(workspace);
        }

        // Inviting again replaces the open invite (and its token).
        await db.StaffInvites.Where(i => i.Email == email && i.AcceptedAt == null).ExecuteDeleteAsync(cancellationToken);

        var token = NewToken();
        var invite = new StaffInvite { Email = email, TokenHash = Hash(token), ExpiresAt = now + Validity };
        db.StaffInvites.Add(invite);

        db.OutboxMessages.Add(OutboxMessage.For(EmailNotification.Queue, InviteEmail(workspace, invite, token, configuration)));
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created($"/api/v1/studio/{tenantSlug}/invites/{invite.Id}", new Invite(invite.Id, invite.Email, invite.ExpiresAt, Expired: false));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> Revoke(
        string tenantSlug, Guid inviteId, ITenantContext tenant, TenancyDbContext db, CancellationToken cancellationToken)
    {
        if (await Check(tenantSlug, tenant, db, cancellationToken) is { } refused)
        {
            return refused.Result is NotFound ? TypedResults.NotFound() : (ProblemHttpResult)refused.Result;
        }

        var deleted = await db.StaffInvites.Where(i => i.Id == inviteId && i.AcceptedAt == null).ExecuteDeleteAsync(cancellationToken);
        return deleted == 0 ? TypedResults.NotFound() : TypedResults.NoContent();
    }

    // Staff members, plus open invites that have not expired (other than one for exceptEmail).
    public static async Task<int> StaffPlacesTakenAsync(TenancyDbContext db, DateTimeOffset now, string? exceptEmail, CancellationToken cancellationToken) =>
        await db.Memberships.CountAsync(m => m.Role == TenantRole.Staff, cancellationToken)
        + await db.StaffInvites.CountAsync(i => i.AcceptedAt == null && i.ExpiresAt > now && i.Email != exceptEmail, cancellationToken);

    public static ProblemHttpResult StaffQuotaReached(Tenant workspace)
    {
        var limit = Plans.Plans.For(workspace.Plan).MaxStaff;
        return TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: $"The {workspace.Plan} plan allows {limit} staff {(limit == 1 ? "member" : "members")}, open invites included.");
    }

    // The link goes to the SPA with the token in the fragment: browsers never send a fragment to a
    // server, so the token stays out of gateway and proxy logs. The SPA posts it in a request body.
    private static EmailNotification InviteEmail(Tenant workspace, StaffInvite invite, string token, IConfiguration configuration)
    {
        var publicUrl = new Uri(configuration["App:PublicUrl"] ?? throw new InvalidOperationException("App:PublicUrl is not configured."));
        return new EmailNotification(EmailTemplates.StaffInvite, To: [invite.Email], ToOwnersOfTenant: null, Locale: "en", new Dictionary<string, string>
        {
            ["workspaceName"] = workspace.DisplayName,
            ["acceptUrl"] = new Uri(publicUrl, $"/invites/{workspace.Slug}#{token}").ToString(),
            ["expiresOn"] = invite.ExpiresAt.UtcDateTime.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture),
        });
    }

    // 404 for another tenant's slug (design §3.3), 403 for staff inside their own workspace.
    private static async Task<Refusal?> Check(string tenantSlug, ITenantContext tenant, TenancyDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenant.TenantId && t.Slug == tenantSlug && t.Status != TenantStatus.Deleted, cancellationToken))
        {
            return new Refusal(TypedResults.NotFound());
        }

        return tenant.Role == TenantRole.Owner
            ? null
            : new Refusal(TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only the owner can manage the team."));
    }

    private sealed record Refusal(IResult Result);

    public static string NewToken() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // An address is matched literally: "_" and "%" are characters here, not wildcards.
    private static string EscapeLike(string text) =>
        text.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
