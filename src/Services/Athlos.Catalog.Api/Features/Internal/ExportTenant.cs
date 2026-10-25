using Athlos.Catalog.Api.Data;
using Athlos.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Internal;

// A workspace's data export (design §5.4), called by tenancy-api for the owner: every program, drafts
// included, with its workouts (the paid content). Not a cross-tenant slice: the handler enters the
// named tenant, so the tenant filter and RLS confine the reads to its rows. Read only.
public static class ExportTenant
{
    public sealed record ExportedWorkout(int Week, int Day, string Title, string Content, string? VideoUrl, bool IsSample);

    public sealed record ExportedProgram(
        Guid Id, string Slug, string Title, string Description, ProgramCategory Category, int DurationWeeks, long PriceAmount, string Currency,
        bool IsPublished, DateTimeOffset? FirstPublishedAt, int RatingCount, decimal RatingAverage, DateTimeOffset CreatedAt, List<ExportedWorkout> Workouts);

    public sealed record Export(List<ExportedProgram> Programs);

    public static void MapExportTenant(this IEndpointRouteBuilder app) =>
        app.MapGet("/tenants/{tenantId:guid}/export", Handle)
            .ExcludeFromDescription();

    private static async Task<IResult> Handle(Guid tenantId, TenantContext tenant, CatalogDbContext db, CancellationToken cancellationToken)
    {
        tenant.EnterTenant(tenantId, role: null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var programs = await db.Programs.AsNoTracking().OrderBy(p => p.CreatedAt).ToListAsync(cancellationToken);
        var workouts = (await db.Workouts.AsNoTracking().OrderBy(w => w.Week).ThenBy(w => w.Day).ToListAsync(cancellationToken)).ToLookup(w => w.ProgramId);
        return TypedResults.Ok(new Export([.. programs.Select(p => new ExportedProgram(
            p.Id, p.Slug, p.Title, p.Description, p.Category, p.DurationWeeks, p.PriceAmount, p.Currency, p.IsPublished, p.FirstPublishedAt,
            p.RatingCount, p.RatingAverage, p.CreatedAt,
            [.. workouts[p.Id].Select(w => new ExportedWorkout(w.Week, w.Day, w.Title, w.Content, w.VideoUrl, w.IsSample))]))]));
    }
}
