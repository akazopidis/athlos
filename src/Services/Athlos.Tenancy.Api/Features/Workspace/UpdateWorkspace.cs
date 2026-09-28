using System.ComponentModel.DataAnnotations;
using Athlos.Tenancy.Api.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Workspace;

// Owners change the workspace name and storefront branding. Staff can read but not change it.
public static class UpdateWorkspace
{
    public sealed record Request(
        [property: Required, StringLength(100, MinimumLength = 1)] string DisplayName,
        [property: Url, StringLength(500)] string? LogoUrl,
        [property: RegularExpression("^#[0-9a-fA-F]{6}$")] string? PrimaryColor);

    public static void MapUpdateWorkspace(this IEndpointRouteBuilder app) =>
        app.MapPut("/workspace", Handle)
            .WithName("UpdateWorkspace")
            .WithTags("Workspace");

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> Handle(
        string tenantSlug, Request request, ITenantContext tenant, TenancyDbContext db, CancellationToken cancellationToken)
    {
        var workspace = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenant.TenantId && t.Slug == tenantSlug, cancellationToken);
        if (workspace is null)
        {
            return TypedResults.NotFound();
        }

        // Inside their own tenant a member may learn the workspace exists, so 403 is fine here.
        if (tenant.Role != TenantRole.Owner)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only the owner can change the workspace.");
        }

        workspace.DisplayName = request.DisplayName.Trim();
        workspace.Branding = new Branding { LogoUrl = request.LogoUrl, PrimaryColor = request.PrimaryColor };
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.NoContent();
    }
}
