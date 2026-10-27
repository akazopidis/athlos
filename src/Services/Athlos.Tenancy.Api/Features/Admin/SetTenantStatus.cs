using Athlos.Tenancy.Api.Data;
using Athlos.Messaging.Contracts;
using Athlos.Tenancy.Api.Features.Tenants;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Admin;

// Suspending or reactivating a workspace (design §3.4), by the platform admin. While suspended:
// - the storefront is 404 and the coach's programs leave the marketplace (catalog-api hides them,
//   in its query and in RLS), so checkout is 404 as well;
// - the coach's studio is closed: the gateway resolves memberships of active workspaces only;
// - customers keep what they bought.
//
// 1. catalog-api hides or shows the programs first. If it fails, nothing has changed here and the
//    admin tries again (502): both steps are idempotent.
// 2. Then the workspace's status changes, in a transaction that enters that tenant, so RLS
//    (tenants_update) lets the write reach this one row only. The same transaction records the
//    admin and their reason (admin_actions), and the suspension email tells the owners the reason.
// Deleting stays with the owner (DeleteWorkspace); an admin deletion route is future work.
public static class SetTenantStatus
{
    public sealed record Request(TenantStatus Status, string? Reason);

    public static void MapSetTenantStatus(this IEndpointRouteBuilder app) =>
        app.MapPut("/tenants/{tenantId:guid}/status", Handle)
            .WithName("AdminSetTenantStatus");

    private static async Task<Results<Ok<ListTenants.Item>, NotFound, ProblemHttpResult>> Handle(
        Guid tenantId, Request request, TenantContext tenant, TenancyDbContext db, ITenantSuspension suspension,
        IConfiguration configuration, TimeProvider time, CancellationToken cancellationToken)
    {
        if (AdminActions.Valid(request.Reason) is not { } reason)
        {
            return AdminActions.MissingReason();
        }

        if (request.Status is not (TenantStatus.Active or TenantStatus.Suspended))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A workspace can be set Active or Suspended.");
        }

        // Tenants are readable by every session (RLS tenants_read), so no transaction is needed yet.
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId && t.Status != TenantStatus.Deleted, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        try
        {
            await suspension.SetAsync(tenantId, suspended: request.Status == TenantStatus.Suspended, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "The coach's programs could not be updated yet. Nothing has changed; try again.");
        }

        tenant.EnterTenant(tenantId, role: null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var workspace = await db.Tenants.SingleAsync(t => t.Id == tenantId, cancellationToken);
        if (workspace.Status != request.Status)
        {
            var suspending = request.Status == TenantStatus.Suspended;
            if (suspending)
            {
                WorkspaceEmails.ToOwners(db, EmailTemplates.WorkspaceSuspended, workspace, configuration, new() { ["reason"] = reason });
                await Reports.ContentReports.CloseCoachReportsAsync(db, workspace.Id, time.GetUtcNow(), cancellationToken);
            }
            else
            {
                WorkspaceEmails.ToOwners(db, EmailTemplates.WorkspaceReactivated, workspace, configuration);
            }

            db.AdminActions.Add(new AdminAction
            {
                AdminId = tenant.UserId ?? Guid.Empty,
                Action = suspending ? AdminActionKind.WorkspaceSuspended : AdminActionKind.WorkspaceReactivated,
                TenantId = workspace.Id,
                Subject = $"{workspace.DisplayName} (/c/{workspace.Slug})",
                Reason = reason,
                At = time.GetUtcNow(),
            });
        }

        workspace.Status = request.Status;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return TypedResults.Ok(ListTenants.ToItem(workspace));
    }
}

// catalog-api's part of a suspension, through its signed internal endpoint.
public interface ITenantSuspension
{
    Task SetAsync(Guid tenantId, bool suspended, CancellationToken cancellationToken);
}

public sealed class ServiceTenantSuspension(IHttpClientFactory clients) : ITenantSuspension
{
    public const string Catalog = "suspend-catalog";

    public async Task SetAsync(Guid tenantId, bool suspended, CancellationToken cancellationToken)
    {
        using var response = await clients.CreateClient(Catalog)
            .PutAsJsonAsync($"/internal/tenants/{tenantId}/suspension", new { suspended }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
