using Athlos.Tenancy.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Storefront;

// Public coach page header: name and branding (design §8, /c/{slug}). No sign-in.
// Suspended and deleted workspaces are 404 (design §3.4).
public static class GetStorefront
{
    public sealed record Response(string Slug, string DisplayName, Branding Branding);

    public static IEndpointRouteBuilder MapGetStorefront(this IEndpointRouteBuilder app)
    {
        app.MapGet("/coaches/{slug}", Handle)
            .WithName("GetStorefront")
            .WithTags("Marketplace");
        return app;
    }

    private static async Task<IResult> Handle(string slug, TenancyDbContext db, CancellationToken cancellationToken)
    {
        var storefront = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Slug == slug && t.Status == TenantStatus.Active)
            .Select(t => new Response(t.Slug, t.DisplayName, t.Branding))
            .SingleOrDefaultAsync(cancellationToken);

        return storefront is null ? TypedResults.NotFound() : TypedResults.Ok(storefront);
    }
}
