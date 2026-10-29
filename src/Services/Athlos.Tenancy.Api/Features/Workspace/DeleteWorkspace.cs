using System.ComponentModel.DataAnnotations;
using Athlos.Tenancy.Api.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Workspace;

// Deleting a workspace (design §3.4, the "right to erasure" part): everything the coach's tenant
// owns goes, by tenant id, in every service. Orders stay in ordering-api for accounting, with the
// workspace marked Deleted here.
//
// Only the owner, who confirms by typing the workspace address; then WorkspaceDeletions runs the
// deletion as one durable job (the platform admin starts the same job, AdminDeleteTenant):
// - ordering-api may still refuse (sales inside the refund window, 409, with the date) and closes
//   open Stripe payment pages, so no customer pays for a program about to be erased;
// - catalog-api hides the programs and the workspace is Suspended, which closes the studio;
// - enrollment-api copies each bought program for its buyers, who keep it read-only with their
//   progress (and drops the workspace's reviews); catalog-api erases the programs and media;
// - tenancy removes the team and invites and marks the tenant Deleted, with its name, branding and
//   Stripe link cleared. The slug stays taken, so no new workspace inherits the old links. The
//   coach's Stripe account is theirs and stays at Stripe.
// 204 when it finished in the request; 202 when a step failed and the retries will finish it.
//
// Not in the studio group's transaction: the calls to other services may wait for a cold start,
// and must not hold one of the service's three database connections meanwhile.
public static class DeleteWorkspace
{
    public sealed record Request([property: Required] string ConfirmSlug);

    public static void MapDeleteWorkspace(this IEndpointRouteBuilder app) =>
        app.MapDelete("/workspace", Handle)
            .WithName("DeleteWorkspace")
            .WithTags("Workspace");

    private static async Task<Results<NoContent, Accepted, NotFound, ProblemHttpResult>> Handle(
        string tenantSlug, [FromBody] Request request, ITenantContext tenant, TenancyDbContext db, IServiceScopeFactory scopes,
        CancellationToken cancellationToken)
    {
        // Tenants are readable by every session (RLS tenants_read), so no transaction is needed yet.
        var workspace = await db.Tenants.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == tenant.TenantId && t.Slug == tenantSlug && t.Status != TenantStatus.Deleted, cancellationToken);
        if (workspace is null)
        {
            return TypedResults.NotFound();
        }

        if (tenant.Role != TenantRole.Owner)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only the owner can delete the workspace.");
        }

        if (!string.Equals(request.ConfirmSlug.Trim(), workspace.Slug, StringComparison.Ordinal))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Type the workspace address to confirm.");
        }

        var (outcome, status, problem) = await WorkspaceDeletions.StartAsync(scopes, workspace.Id, tenant.UserId!.Value, audit: null, cancellationToken);
        return outcome switch
        {
            WorkspaceDeletions.Outcome.Deleted => TypedResults.NoContent(),
            WorkspaceDeletions.Outcome.Pending => TypedResults.Accepted((string?)null),
            _ => TypedResults.Problem(statusCode: status, title: problem),
        };
    }
}

// The other services' part of a workspace deletion, through their signed internal endpoints.
public interface ITenantErasure
{
    // ordering-api's check before anything is erased: null if the workspace may go, or why not.
    Task<string?> PrepareAsync(Guid tenantId, CancellationToken cancellationToken);

    Task EraseAsync(Guid tenantId, CancellationToken cancellationToken);
}

public sealed class ServiceTenantErasure(IHttpClientFactory clients) : ITenantErasure
{
    public const string Catalog = "erase-catalog";
    public const string Enrollment = "erase-enrollment";
    public const string Ordering = "prepare-erase-ordering";

    public async Task<string?> PrepareAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        using var response = await clients.CreateClient(Ordering).PostAsync($"/internal/tenants/{tenantId}/prepare-deletion", null, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
            return problem?.Title ?? "The workspace cannot be deleted yet.";
        }

        response.EnsureSuccessStatusCode();
        return null;
    }

    public async Task EraseAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // Enrollment first: it copies the programs customers bought from catalog, before catalog erases them.
        foreach (var service in (string[])[Enrollment, Catalog])
        {
            using var response = await clients.CreateClient(service).DeleteAsync($"/internal/tenants/{tenantId}", cancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }
}
