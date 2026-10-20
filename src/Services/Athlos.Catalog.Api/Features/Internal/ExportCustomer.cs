using Athlos.Catalog.Api.Data;
using Athlos.Tenancy;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Internal;

// A customer's data export (design §5.4), called by tenancy-api: the programs they saved, with each
// one's title while it is still on sale.
//
// CROSS-TENANT SLICE (design §3.2, "a customer's own rows, for their export"): acts for that user in
// the public-read scope, so RLS shows their saved rows and published programs only; the program
// titles ignore the tenant filter like the shop's. Read only.
public static class ExportCustomer
{
    public sealed record ExportedSave(Guid ProgramId, string? ProgramTitle, string? CoachSlug, DateTimeOffset SavedAt);

    public sealed record Export(List<ExportedSave> Saved);

    public static void MapExportCustomer(this IEndpointRouteBuilder app) =>
        app.MapGet("/customers/{customerId:guid}/export", Handle)
            .ExcludeFromDescription();

    private static async Task<IResult> Handle(Guid customerId, TenantContext tenant, CatalogDbContext db, CancellationToken cancellationToken)
    {
        tenant.ActForUser(customerId);
        tenant.UsePublicReadScope();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var programs = db.Programs.AsNoTracking().IgnoreQueryFilters([TenantDbContext.TenantFilter]).Where(p => p.IsPublished && !p.TenantSuspended);
        var saved = await (
                from s in db.SavedPrograms.AsNoTracking()
                where s.CustomerId == customerId
                join p in programs on s.ProgramId equals p.Id into onSale
                from p in onSale.DefaultIfEmpty()
                orderby s.SavedAt
                select new ExportedSave(s.ProgramId, p == null ? null : p.Title, p == null ? null : p.CoachSlug, s.SavedAt))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new Export(saved));
    }
}
