using System.ComponentModel.DataAnnotations;
using Athlos.Tenancy.Api.Data;
using Athlos.Tenancy.Api.Features.Tenants;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Workspace;

// POST /api/v1/studio/{slug}/workspace/coach-terms (design §5.4): the owner accepts the Terms for
// coaches in force, for a workspace opened before them or before they changed. The studio asks until
// the workspace has accepted the current version; selling is not blocked meanwhile. Recorded like
// the acceptance when the workspace was opened: the version, the time and the user.
public static class AcceptCoachTerms
{
    public sealed record Request([property: Required, MaxLength(20)] string TermsVersion);

    public static void MapAcceptCoachTerms(this IEndpointRouteBuilder app) =>
        app.MapPost("/workspace/coach-terms", Handle)
            .WithName("AcceptCoachTerms")
            .WithTags("Workspace");

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> Handle(
        string tenantSlug, Request request, ITenantContext tenant, TenancyDbContext db, IConfiguration configuration, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var workspace = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenant.TenantId && t.Slug == tenantSlug && t.Status != TenantStatus.Deleted, cancellationToken);
        if (workspace is null)
        {
            return TypedResults.NotFound();
        }

        // Inside their own tenant a member may learn the workspace exists, so 403 is fine here.
        if (tenant.Role != TenantRole.Owner)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only the owner can accept the Terms for coaches.");
        }

        var terms = CreateTenant.CoachTermsInForce(configuration);
        if (request.TermsVersion != terms)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Read and accept the current Terms for coaches.",
                extensions: new Dictionary<string, object?> { ["reason"] = "terms" });
        }

        workspace.CoachTermsVersion = terms;
        workspace.CoachTermsAcceptedAt = time.GetUtcNow();
        workspace.CoachTermsAcceptedBy = tenant.UserId;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
