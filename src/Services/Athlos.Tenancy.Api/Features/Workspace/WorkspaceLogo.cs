using Athlos.Media;
using Athlos.Tenancy.Api.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Workspace;

// The coach's logo or photo (design §8), uploaded rather than linked: the request body is the image
// itself (JPEG, PNG or WebP, at most 2 MB), like a program cover. Owners only. The blob path is built from
// the tenant in ITenantContext, never from input: tenants/{tenantId}/workspace/logo-{id}.{ext}, so a
// deleted workspace's logo goes with the rest of its media (catalog-api erases the tenant's prefix).
// Storefronts hand out a 15-minute read link for it, in place of a linked logo URL.
public static class WorkspaceLogo
{
    public static void MapWorkspaceLogo(this IEndpointRouteBuilder studio)
    {
        studio.MapPut("/workspace/logo", Upload)
            .Accepts<IFormFile>(ImageType.Jpeg.ContentType, ImageType.Png.ContentType, ImageType.Webp.ContentType)
            .WithName("UploadWorkspaceLogo")
            .WithTags("Workspace");
        studio.MapDelete("/workspace/logo", Delete)
            .WithName("DeleteWorkspaceLogo")
            .WithTags("Workspace");
    }

    // A new name per upload, so browsers and caches never show an old logo.
    public static string LogoPath(Guid tenantId, ImageType type) =>
        $"tenants/{tenantId}/workspace/logo-{Guid.CreateVersion7():N}.{type.Extension}";

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> Upload(
        string tenantSlug, HttpRequest request, ITenantContext tenant, TenancyDbContext db, MediaStore media, CancellationToken cancellationToken)
    {
        if (await OwnWorkspaceAsync(tenantSlug, tenant, db, cancellationToken) is not { } workspace)
        {
            return TypedResults.NotFound();
        }

        if (tenant.Role != TenantRole.Owner)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only the owner can change the logo.");
        }

        if (request.ContentLength > MediaStore.MaxImageBytes)
        {
            return TooLarge();
        }

        // Read at most what is allowed, whatever Content-Length claimed.
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > MediaStore.MaxImageBytes)
        {
            return TooLarge();
        }

        var bytes = buffer.ToArray();
        if (ImageType.Detect(bytes) is not { } type)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "The logo must be a JPEG, PNG or WebP image.");
        }

        var previous = workspace.Branding.LogoPath;
        var path = LogoPath(tenant.TenantId!.Value, type);
        await media.UploadAsync(path, BinaryData.FromBytes(bytes), type, cancellationToken);
        workspace.Branding = workspace.Branding.With(logoPath: path);
        await db.SaveChangesAsync(cancellationToken);

        if (previous is not null)
        {
            await media.DeleteAsync(previous, cancellationToken);
        }

        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> Delete(
        string tenantSlug, ITenantContext tenant, TenancyDbContext db, MediaStore media, CancellationToken cancellationToken)
    {
        if (await OwnWorkspaceAsync(tenantSlug, tenant, db, cancellationToken) is not { } workspace)
        {
            return TypedResults.NotFound();
        }

        if (tenant.Role != TenantRole.Owner)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only the owner can change the logo.");
        }

        if (workspace.Branding.LogoPath is { } path)
        {
            workspace.Branding = workspace.Branding.With(logoPath: null);
            await db.SaveChangesAsync(cancellationToken);
            await media.DeleteAsync(path, cancellationToken);
        }

        return TypedResults.NoContent();
    }

    // The route's slug must be the tenant the gateway resolved, and not deleted: otherwise 404.
    private static Task<Tenant?> OwnWorkspaceAsync(string tenantSlug, ITenantContext tenant, TenancyDbContext db, CancellationToken cancellationToken) =>
        db.Tenants.SingleOrDefaultAsync(t => t.Id == tenant.TenantId && t.Slug == tenantSlug && t.Status != TenantStatus.Deleted, cancellationToken);

    private static ProblemHttpResult TooLarge() =>
        TypedResults.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "The logo can be at most 2 MB.");
}
