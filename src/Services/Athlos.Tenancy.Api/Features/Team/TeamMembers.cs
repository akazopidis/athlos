using Athlos.Tenancy.Api.Data;
using Athlos.Messaging.Contracts;
using Athlos.Tenancy.Api.Features.Tenants;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Team;

// The workspace's team (design §5.2): who is in it, removing staff, leaving, and handing ownership over.
// - Every member sees the team; only the owner sees addresses (they invited them).
// - The owner removes staff; a staff member removes themselves (leaves). The owner cannot leave: they
//   hand ownership to a staff member first, or delete the workspace.
// - Handing over swaps the two roles in one transaction, so a workspace always has exactly one owner.
//   It also disconnects the workspace's Stripe account: that account belongs to the person who set it
//   up and pays out to their bank, so it must not keep receiving the new owner's sales. Sales pause
//   (checkout finds no payout account) until the new owner sets up payouts with their own account.
//   Earlier sales are unaffected: each order names the account it pays (SellerAccountId), so their
//   pending payouts and refunds still go to, and come from, the previous owner's account.
// All of it runs inside the tenant (membership RLS), so another tenant's member is simply not found.
// The gateway caches memberships for up to 60 seconds, so a change reaches it within a minute.
public static class TeamMembers
{
    public sealed record Member(Guid UserId, string DisplayName, string? Email, TenantRole Role, bool IsYou);

    public static void MapTeamMembers(this IEndpointRouteBuilder studio)
    {
        var members = studio.MapGroup("/members").WithTags("Team");
        members.MapGet("/", List).WithName("ListTeamMembers");
        members.MapDelete("/{userId:guid}", Remove).WithName("RemoveTeamMember");
        members.MapPost("/{userId:guid}/ownership", HandOver).WithName("TransferOwnership");
    }

    private static async Task<Results<Ok<List<Member>>, NotFound>> List(
        string tenantSlug, ITenantContext tenant, TenancyDbContext db, CancellationToken cancellationToken)
    {
        if (!await IsThisWorkspaceAsync(tenantSlug, tenant, db, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        // Members' profiles are readable inside the tenant (user_profiles tenant_members_read).
        var owner = tenant.Role == TenantRole.Owner;
        var team = await db.Memberships.AsNoTracking()
            .GroupJoin(db.UserProfiles, m => m.UserId, p => p.UserId, (m, profiles) => new { m, profiles })
            .SelectMany(x => x.profiles.DefaultIfEmpty(), (x, p) => new { x.m.UserId, x.m.Role, x.m.CreatedAt, p!.DisplayName, p.Email })
            .OrderBy(x => x.Role)
            .ThenBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(team
            .Select(x => new Member(x.UserId, x.DisplayName ?? "Unknown member", owner ? x.Email : null, x.Role, x.UserId == tenant.UserId))
            .ToList());
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> Remove(
        string tenantSlug, Guid userId, ITenantContext tenant, TenancyDbContext db, IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!await IsThisWorkspaceAsync(tenantSlug, tenant, db, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var leaving = userId == tenant.UserId;
        if (!leaving && tenant.Role != TenantRole.Owner)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only the owner can remove members.");
        }

        var member = await db.Memberships.SingleOrDefaultAsync(m => m.UserId == userId, cancellationToken);
        if (member is null)
        {
            return TypedResults.NotFound();
        }

        if (member.Role == TenantRole.Owner)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "The owner cannot leave the workspace. Hand ownership to a staff member first, or delete the workspace.");
        }

        // The owners hear that someone left; someone removed hears it themselves (profiles are readable
        // inside the tenant, tenant_members_read).
        var workspace = await db.Tenants.SingleAsync(t => t.Id == tenant.TenantId, cancellationToken);
        var profile = await db.UserProfiles.Where(p => p.UserId == userId).Select(p => new { p.DisplayName, p.Email }).SingleOrDefaultAsync(cancellationToken);
        if (leaving)
        {
            WorkspaceEmails.ToOwners(db, EmailTemplates.MemberLeft, workspace, configuration,
                new() { ["memberName"] = string.IsNullOrWhiteSpace(profile?.DisplayName) ? "A team member" : profile.DisplayName });
        }
        else if (profile is not null)
        {
            WorkspaceEmails.ToMember(db, EmailTemplates.MemberRemoved, profile.Email, workspace, configuration);
        }

        db.Memberships.Remove(member);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> HandOver(
        string tenantSlug, Guid userId, ITenantContext tenant, TenancyDbContext db, IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!await IsThisWorkspaceAsync(tenantSlug, tenant, db, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        if (tenant.Role != TenantRole.Owner)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only the owner can hand ownership over.");
        }

        var members = await db.Memberships.Where(m => m.UserId == userId || m.UserId == tenant.UserId).ToListAsync(cancellationToken);
        var next = members.SingleOrDefault(m => m.UserId == userId && m.Role == TenantRole.Staff);
        var current = members.SingleOrDefault(m => m.UserId == tenant.UserId && m.Role == TenantRole.Owner);
        if (next is null || current is null)
        {
            return TypedResults.NotFound();
        }

        next.Role = TenantRole.Owner;
        current.Role = TenantRole.Staff;
        var workspace = await db.Tenants.SingleAsync(t => t.Id == tenant.TenantId, cancellationToken);
        workspace.StripeAccountId = null;
        workspace.StripeOnboardingComplete = false;
        // Sent to the owners when the worker sends it: by then, the new owner.
        WorkspaceEmails.ToOwners(db, EmailTemplates.OwnershipReceived, workspace, configuration);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    // The slug in the route must be the tenant the gateway resolved, and not deleted: otherwise 404.
    private static Task<bool> IsThisWorkspaceAsync(string tenantSlug, ITenantContext tenant, TenancyDbContext db, CancellationToken cancellationToken) =>
        db.Tenants.AnyAsync(t => t.Id == tenant.TenantId && t.Slug == tenantSlug && t.Status != TenantStatus.Deleted, cancellationToken);
}
