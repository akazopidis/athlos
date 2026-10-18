using Athlos.Media;
using Athlos.Tenancy.Api.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Storefront;

// Public coach page header: name and profile (design §8, /c/{slug}): logo, colors, contact address, bio
// and links. No sign-in.
// Suspended and deleted workspaces are 404 (design §3.4).
public static class GetStorefront
{
    public sealed record Response(string Slug, string DisplayName, CoachProfile Branding);

    public static IEndpointRouteBuilder MapGetStorefront(this IEndpointRouteBuilder app)
    {
        app.MapGet("/coaches/{slug}", Handle)
            .WithName("GetStorefront")
            .WithTags("Marketplace");
        return app;
    }

    private static async Task<Results<Ok<Response>, NotFound>> Handle(string slug, TenancyDbContext db, MediaStore media, CancellationToken cancellationToken)
    {
        var storefront = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Slug == slug && t.Status == TenantStatus.Active)
            .Select(t => new { t.Slug, t.DisplayName, t.Branding })
            .SingleOrDefaultAsync(cancellationToken);

        return storefront is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new Response(storefront.Slug, storefront.DisplayName, await CoachProfile.FromAsync(storefront.Branding, media, cancellationToken)));
    }
}
