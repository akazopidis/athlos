using Athlos.Catalog.Api.Data;
using Athlos.Media;
using Athlos.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Internal;

// For enrollment-api (design §2.3, enrollment notes): a program's paid content, its workouts.
// enrollment-api decides who may see it (the caller's own enrollment) and names the tenant from that
// enrollment; catalog only serves it. Not a cross-tenant slice: the handler enters that one tenant
// before its transaction, so the tenant filter and RLS read exactly that tenant's rows, as for the
// coach. Published or not: customers keep what they bought when a program is unpublished (§3.4).
public static class GetProgramContent
{
    public sealed record Workout(Guid Id, int Week, int Day, string Title, string Content, string? VideoUrl);

    public sealed record Response(
        Guid ProgramId, string CoachSlug, string Title, string Description, ProgramCategory Category, int DurationWeeks, string? CoverImageUrl, List<Workout> Workouts);

    public static void MapGetProgramContent(this IEndpointRouteBuilder app) =>
        app.MapGet("/tenants/{tenantId:guid}/programs/{programId:guid}/content", Handle)
            .ExcludeFromDescription();

    private static async Task<Results<Ok<Response>, NotFound>> Handle(
        Guid tenantId, Guid programId, TenantContext tenant, CatalogDbContext db, MediaStore media, CancellationToken cancellationToken)
    {
        tenant.EnterTenant(tenantId, role: null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var program = await db.Programs.AsNoTracking().SingleOrDefaultAsync(p => p.Id == programId, cancellationToken);
        if (program is null)
        {
            return TypedResults.NotFound();
        }

        var workouts = await db.Workouts.AsNoTracking()
            .Where(w => w.ProgramId == programId)
            .OrderBy(w => w.Week).ThenBy(w => w.Day)
            .Select(w => new Workout(w.Id, w.Week, w.Day, w.Title, w.Content, w.VideoUrl))
            .ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return TypedResults.Ok(new Response(program.Id, program.CoachSlug, program.Title, program.Description, program.Category,
            program.DurationWeeks, await media.ReadLinkAsync(program.CoverImagePath, cancellationToken), workouts));
    }
}
