using Athlos.Enrollment.Api.Data;
using Athlos.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Enrollment.Api.Features.Enrollments;

// A workspace's data export (design §5.4), called by tenancy-api for the owner: the reviews of its
// programs, hidden ones marked, with the coach's replies, and how many customers each program has.
// Not a cross-tenant slice: the handler enters the named tenant, so the tenant filter and RLS confine
// the reads to its rows. Read only; no customer ids or addresses.
public static class ExportTenant
{
    public sealed record ExportedReview(
        Guid ProgramId, string ProgramTitle, int Rating, string? Comment, string AuthorName, string? Reply, DateTimeOffset CreatedAt, DateTimeOffset? HiddenAt);

    public sealed record ExportedProgram(Guid ProgramId, string ProgramTitle, int Customers);

    public sealed record Export(List<ExportedProgram> Programs, List<ExportedReview> Reviews);

    public static void MapExportTenant(this IEndpointRouteBuilder app) =>
        app.MapGet("/internal/tenants/{tenantId:guid}/export", Handle)
            .ExcludeFromDescription();

    private static async Task<IResult> Handle(Guid tenantId, TenantContext tenant, EnrollmentDbContext db, CancellationToken cancellationToken)
    {
        tenant.EnterTenant(tenantId, role: null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var programs = await db.Enrollments.AsNoTracking()
            .GroupBy(e => new { e.ProgramId, e.ProgramTitle })
            .OrderBy(g => g.Key.ProgramTitle)
            .Select(g => new ExportedProgram(g.Key.ProgramId, g.Key.ProgramTitle, g.Count()))
            .ToListAsync(cancellationToken);
        var reviews = await (
                from r in db.Reviews.AsNoTracking()
                join e in db.Enrollments.AsNoTracking() on r.EnrollmentId equals e.Id
                orderby r.CreatedAt
                select new ExportedReview(r.ProgramId, e.ProgramTitle, r.Rating, r.Comment, r.AuthorName, r.Reply, r.CreatedAt, r.HiddenAt))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new Export(programs, reviews));
    }
}
