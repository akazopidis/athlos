using Athlos.Tenancy.Api.Data;
using Athlos.Messaging.Contracts;
using Athlos.Tenancy.Api.Features.Tenants;
using Athlos.Tenancy.Api.Features.Plans;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Admin;

// Assigning a workspace's plan (design §3.4: "Plans (Free, Pro) are assigned by the platform admin").
// The plan drives the gateway's rate limits and the program and staff quotas (design §4); the gateway
// caches it for about 60 seconds, so a change can take a minute to apply there. A downgrade keeps
// what the coach already has; the quotas only stop new programs and staff.
// The transaction enters that tenant, so RLS (tenants_update) lets the write reach this one row only.
public static class SetTenantPlan
{
    public sealed record Request(PlanName Plan);

    public static void MapSetTenantPlan(this IEndpointRouteBuilder app) =>
        app.MapPut("/tenants/{tenantId:guid}/plan", Handle)
            .WithName("AdminSetTenantPlan");

    private static async Task<Results<Ok<ListTenants.Item>, NotFound>> Handle(
        Guid tenantId, Request request, TenantContext tenant, TenancyDbContext db, IConfiguration configuration, CancellationToken cancellationToken)
    {
        tenant.EnterTenant(tenantId, role: null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var workspace = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId && t.Status != TenantStatus.Deleted, cancellationToken);
        if (workspace is null)
        {
            return TypedResults.NotFound();
        }

        if (workspace.Plan != request.Plan)
        {
            var plan = Plans.Plans.For(request.Plan);
            WorkspaceEmails.ToOwners(db, EmailTemplates.PlanChanged, workspace, configuration, new()
            {
                ["plan"] = request.Plan.ToString(),
                ["maxPrograms"] = plan.MaxPrograms.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["maxStaff"] = plan.MaxStaff.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
        }

        workspace.Plan = request.Plan;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return TypedResults.Ok(ListTenants.ToItem(workspace));
    }
}
