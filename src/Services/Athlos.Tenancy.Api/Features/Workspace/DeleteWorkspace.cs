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
// 1. Only the owner, who confirms by typing the workspace address.
// 2. catalog-api and enrollment-api erase their rows (and catalog the media) first. If one fails,
//    the workspace is still Active and the owner can try again: every step is idempotent.
// 3. Then, in one transaction, tenancy removes the team and invites and marks the tenant Deleted,
//    with its name, branding and Stripe link cleared. The slug stays taken, so no new workspace
//    inherits the old links. The coach's Stripe account is theirs and stays at Stripe.
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

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> Handle(
        string tenantSlug, [FromBody] Request request, ITenantContext tenant, TenancyDbContext db, ITenantErasure erasure,
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

        try
        {
            await erasure.EraseAsync(workspace.Id, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Some of the workspace could not be deleted yet. Nothing is lost by trying again.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.StaffInvites.ExecuteDeleteAsync(cancellationToken);
        await db.Memberships.ExecuteDeleteAsync(cancellationToken);
        var deleted = await db.Tenants.SingleAsync(t => t.Id == workspace.Id, cancellationToken);
        deleted.Status = TenantStatus.Deleted;
        deleted.DisplayName = "Deleted workspace";
        deleted.Branding = new Branding();
        deleted.StripeAccountId = null;
        deleted.StripeOnboardingComplete = false;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return TypedResults.NoContent();
    }
}

// The other services' part of a workspace deletion, through their signed internal endpoints.
public interface ITenantErasure
{
    Task EraseAsync(Guid tenantId, CancellationToken cancellationToken);
}

public sealed class ServiceTenantErasure(IHttpClientFactory clients) : ITenantErasure
{
    public const string Catalog = "erase-catalog";
    public const string Enrollment = "erase-enrollment";

    public async Task EraseAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // Catalog first: the programs leave the marketplace before anything else goes.
        foreach (var service in (string[])[Catalog, Enrollment])
        {
            using var response = await clients.CreateClient(service).DeleteAsync($"/internal/tenants/{tenantId}", cancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }
}
