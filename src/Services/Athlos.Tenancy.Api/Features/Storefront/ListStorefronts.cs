using Athlos.Tenancy.Api.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Storefront;

// Several coaches' names and branding at once, by address, for the shop's product cards and coach
// filter (design §8): catalog knows a program's coach only by slug. Public; active workspaces only,
// so a suspended or deleted coach is simply missing from the answer.
public static class ListStorefronts
{
    public const int MaxSlugs = 50;

    public static IEndpointRouteBuilder MapListStorefronts(this IEndpointRouteBuilder app)
    {
        app.MapGet("/coaches", Handle)
            .WithName("ListStorefronts")
            .WithTags("Marketplace");
        return app;
    }

    private static async Task<Results<Ok<List<GetStorefront.Response>>, ProblemHttpResult>> Handle(
        string[]? slug, TenancyDbContext db, CancellationToken cancellationToken)
    {
        var slugs = (slug ?? []).Distinct().ToList();
        if (slugs.Count > MaxSlugs)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: $"Ask for at most {MaxSlugs} coaches at a time.");
        }

        // Tenants are readable by every session (RLS tenants_read).
        var storefronts = await db.Tenants
            .AsNoTracking()
            .Where(t => slugs.Contains(t.Slug) && t.Status == TenantStatus.Active)
            .OrderBy(t => t.DisplayName)
            .Select(t => new GetStorefront.Response(t.Slug, t.DisplayName, t.Branding))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(storefronts);
    }
}
