using Athlos.Tenancy.Api.Data;
using Athlos.Tenancy.Api.Features.Workspace;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Admin;

// The platform admin deletes a workspace (design §3.4, backlog item 54), with a reason in the audit
// trail: the same durable job as the owner's deletion (WorkspaceDeletions), under the same rules. The
// refund window still protects the customers (409), and the buyers keep a read-only copy.
// POST /api/v1/admin/tenants/{tenantId}/deletion: 204 when done, 202 when the retries will finish it.
public static class AdminDeleteTenant
{
    public sealed record Request(string? Reason);

    public static void MapAdminDeleteTenant(this IEndpointRouteBuilder app) =>
        app.MapPost("/tenants/{tenantId:guid}/deletion", Handle)
            .WithName("AdminDeleteTenant");

    private static async Task<Results<NoContent, Accepted, NotFound, ProblemHttpResult>> Handle(
        Guid tenantId, Request request, ITenantContext tenant, TenancyDbContext db, IServiceScopeFactory scopes, TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (AdminActions.Valid(request.Reason) is not { } reason)
        {
            return AdminActions.MissingReason();
        }

        // Tenants are readable by every session (RLS tenants_read).
        var workspace = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tenantId && t.Status != TenantStatus.Deleted, cancellationToken);
        if (workspace is null)
        {
            return TypedResults.NotFound();
        }

        var audit = new AdminAction
        {
            AdminId = tenant.UserId ?? Guid.Empty,
            Action = AdminActionKind.WorkspaceDeleted,
            TenantId = workspace.Id,
            Subject = $"{workspace.DisplayName} (/c/{workspace.Slug})",
            Reason = reason,
            At = time.GetUtcNow(),
        };
        var (outcome, status, problem) = await WorkspaceDeletions.StartAsync(scopes, workspace.Id, audit.AdminId, audit, cancellationToken);
        return outcome switch
        {
            WorkspaceDeletions.Outcome.Deleted => TypedResults.NoContent(),
            WorkspaceDeletions.Outcome.Pending => TypedResults.Accepted((string?)null),
            _ => TypedResults.Problem(statusCode: status, title: problem),
        };
    }
}
