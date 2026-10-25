using Athlos.Media;
using Athlos.Tenancy.Api.Data;
using Athlos.Tenancy.Api.Features.Plans;
using Athlos.Tenancy.Api.Features.Tenants;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Workspace;

// Workspace settings for the coach dashboard. The tenant comes from the gateway (it resolved the
// slug and checked membership); the slug in the route must match it, or it is someone else's: 404.
public static class GetWorkspace
{
    // CoachTermsVersion: the Terms for coaches the workspace accepted (null if none, design §5.4);
    // CoachTermsCurrent: whether that is the version in force (Legal:CoachTermsVersion).
    public sealed record Response(
        Guid Id, string Slug, string DisplayName, TenantStatus Status, PlanName Plan, Settings Branding, bool StripeOnboardingComplete,
        string? CoachTermsVersion, bool CoachTermsCurrent);

    // What the owner edits: the linked logo as typed, and the uploaded one's read link separately.
    public sealed record Settings(
        string? LogoUrl, string? UploadedLogoUrl, string? PrimaryColor, string? ContactEmail, string? Bio, string? InstagramUrl, string? YouTubeUrl,
        string? WebsiteUrl);

    public static void MapGetWorkspace(this IEndpointRouteBuilder app) =>
        app.MapGet("/workspace", Handle)
            .WithName("GetWorkspace")
            .WithTags("Workspace");

    private static async Task<Results<Ok<Response>, NotFound>> Handle(
        string tenantSlug, ITenantContext tenant, TenancyDbContext db, MediaStore media, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var workspace = await db.Tenants
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == tenant.TenantId && t.Slug == tenantSlug && t.Status != TenantStatus.Deleted, cancellationToken);
        if (workspace is null)
        {
            return TypedResults.NotFound();
        }

        var b = workspace.Branding;
        var settings = new Settings(b.LogoUrl, await media.ReadLinkAsync(b.LogoPath, cancellationToken), b.PrimaryColor, b.ContactEmail, b.Bio,
            b.InstagramUrl, b.YouTubeUrl, b.WebsiteUrl);
        return TypedResults.Ok(new Response(workspace.Id, workspace.Slug, workspace.DisplayName, workspace.Status, workspace.Plan, settings,
            workspace.StripeOnboardingComplete, workspace.CoachTermsVersion, workspace.CoachTermsVersion == CreateTenant.CoachTermsInForce(configuration)));
    }
}
