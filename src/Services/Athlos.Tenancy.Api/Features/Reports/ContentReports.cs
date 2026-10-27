using System.ComponentModel.DataAnnotations;
using System.Net;
using Athlos.Messaging.Contracts;
using Athlos.Tenancy.Api.Data;
using Athlos.Tenancy.Api.Features.Tenants;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Reports;

// Reporting a program or a coach (design §2.4), the notice and action the EU Digital Services Act asks
// of a platform. Anyone signed in reports a program on sale or an active coach's storefront, with a
// reason and an optional note, once per program or coach. The platform admin sees open reports, one
// entry per program or coach, and decides, always with a reason (audit trail, §3.4):
// - a program: take it off the marketplace (catalog-api unpublishes and blocks it, and the coach is
//   emailed the reason), keep it, or later allow a taken-down program back;
// - a coach: suspend the workspace (SetTenantStatus, which closes the coach's reports and emails the
//   reason), or keep it.
// content_reports is a work queue of the service (allow-all policy, like the outbox): it copies what
// the admin needs, so no tenant's rows are read across tenants. Programs come from catalog-api's
// signed internal endpoints.
public static class ContentReports
{
    public sealed record ReportRequest(ContentReportKind Kind, Guid? ProgramId, [property: MaxLength(40)] string? CoachSlug, ContentReportReason Reason,
        [property: MaxLength(500)] string? Note);

    public sealed record Decision(string? Reason);

    public sealed record OpenReport(
        ContentReportKind Kind, Guid TenantId, Guid? ProgramId, string Subject, string CoachSlug, int Reports, List<ContentReportReason> Reasons, List<string> Notes,
        DateTimeOffset FirstReportedAt);

    public sealed record TakenDown(Guid ProgramId, Guid TenantId, string Subject, string CoachSlug, DateTimeOffset Since);

    public sealed record ReportList(List<OpenReport> Open, List<TakenDown> TakenDown);

    // POST /api/v1/me/reports: 204 whether or not this person reported it before. A program not on sale
    // or a coach not active is 404.
    public static void MapReportContent(this IEndpointRouteBuilder me) =>
        me.MapPost("/reports", ReportAsync)
            .WithName("ReportContent")
            .WithTags("Reports");

    public static void MapAdminReports(this IEndpointRouteBuilder admin)
    {
        admin.MapGet("/reports", ListAsync).WithName("AdminListReports");
        admin.MapPost("/reports/programs/{programId:guid}/unpublish", (Guid programId, Decision request, TenantContext tenant, TenancyDbContext db,
                IReportedPrograms programs, IConfiguration configuration, TimeProvider time, CancellationToken cancellationToken) =>
                UnpublishAsync(programId, request, tenant, db, programs, configuration, time, cancellationToken))
            .WithName("AdminUnpublishProgram");
        admin.MapPost("/reports/programs/{programId:guid}/allow", (Guid programId, Decision request, TenantContext tenant, TenancyDbContext db,
                IReportedPrograms programs, TimeProvider time, CancellationToken cancellationToken) =>
                AllowAsync(programId, request, tenant, db, programs, time, cancellationToken))
            .WithName("AdminAllowProgram");
        admin.MapPost("/reports/programs/{programId:guid}/keep", (Guid programId, Decision request, TenantContext tenant, TenancyDbContext db, TimeProvider time,
                CancellationToken cancellationToken) => KeepAsync(r => r.Kind == ContentReportKind.Program && r.ProgramId == programId, request, tenant, db, time, cancellationToken))
            .WithName("AdminKeepProgram");
        admin.MapPost("/reports/coaches/{tenantId:guid}/keep", (Guid tenantId, Decision request, TenantContext tenant, TenancyDbContext db, TimeProvider time,
                CancellationToken cancellationToken) => KeepAsync(r => r.Kind == ContentReportKind.Coach && r.TenantId == tenantId, request, tenant, db, time, cancellationToken))
            .WithName("AdminKeepCoach");
    }

    // Closes a workspace's open coach reports when the admin suspends it (SetTenantStatus), in the
    // suspension's transaction.
    public static Task CloseCoachReportsAsync(TenancyDbContext db, Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.ContentReports
            .Where(r => r.Kind == ContentReportKind.Coach && r.TenantId == tenantId && r.ResolvedAt == null)
            .ExecuteUpdateAsync(r => r.SetProperty(x => x.ResolvedAt, now).SetProperty(x => x.Outcome, ContentReportOutcome.Suspended), cancellationToken);

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> ReportAsync(
        ReportRequest request, TenantContext tenant, TenancyDbContext db, IReportedPrograms programs, TimeProvider time, CancellationToken cancellationToken)
    {
        if (tenant.UserId is not { } reporter)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized);
        }

        Guid tenantId;
        string subject;
        string coachSlug;
        if (request.Kind == ContentReportKind.Program)
        {
            if (request.ProgramId is not { } programId || await programs.FindOnSaleAsync(programId, cancellationToken) is not { } program)
            {
                return TypedResults.NotFound();
            }

            (tenantId, subject, coachSlug) = (program.TenantId, program.Title, program.CoachSlug);
        }
        else
        {
            // Tenants are readable by every session (RLS tenants_read).
            var coach = await db.Tenants.AsNoTracking()
                .Where(t => t.Slug == request.CoachSlug && t.Status == TenantStatus.Active)
                .Select(t => new { t.Id, t.DisplayName, t.Slug })
                .SingleOrDefaultAsync(cancellationToken);
            if (coach is null)
            {
                return TypedResults.NotFound();
            }

            (tenantId, subject, coachSlug) = (coach.Id, coach.DisplayName, coach.Slug);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var programIdOrNull = request.Kind == ContentReportKind.Program ? request.ProgramId : null;
        var reportedBefore = await db.ContentReports.AnyAsync(r =>
            r.Kind == request.Kind && r.TenantId == tenantId && r.ProgramId == programIdOrNull && r.ReporterId == reporter && r.ResolvedAt == null, cancellationToken);
        if (!reportedBefore)
        {
            db.ContentReports.Add(new ContentReport
            {
                Kind = request.Kind,
                TenantId = tenantId,
                ProgramId = programIdOrNull,
                Subject = subject,
                CoachSlug = coachSlug,
                ReporterId = reporter,
                Reason = request.Reason,
                Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
                ReportedAt = time.GetUtcNow(),
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    // Open reports, one entry per program or coach, oldest first; and the programs taken down, so the
    // admin can allow one back.
    private static async Task<Ok<ReportList>> ListAsync(TenancyDbContext db, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var open = await db.ContentReports.AsNoTracking()
            .Where(r => r.ResolvedAt == null)
            .OrderBy(r => r.ReportedAt)
            .Take(500)
            .ToListAsync(cancellationToken);
        var takenDown = await db.ContentReports.AsNoTracking()
            .Where(r => r.Kind == ContentReportKind.Program && r.Outcome == ContentReportOutcome.Unpublished)
            .GroupBy(r => new { r.ProgramId, r.TenantId, r.Subject, r.CoachSlug })
            .Select(g => new { g.Key.ProgramId, g.Key.TenantId, g.Key.Subject, g.Key.CoachSlug, Since = g.Max(r => r.ResolvedAt) })
            .OrderByDescending(t => t.Since)
            .Take(100)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new ReportList(
            [.. open
                .GroupBy(r => (r.Kind, r.TenantId, r.ProgramId))
                .Select(g =>
                {
                    var first = g.First();
                    return new OpenReport(first.Kind, first.TenantId, first.ProgramId, first.Subject, first.CoachSlug, g.Count(),
                        [.. g.Select(r => r.Reason).Distinct()], [.. g.Where(r => r.Note != null).Select(r => r.Note!)], first.ReportedAt);
                })
                .Take(100)],
            [.. takenDown.Select(t => new TakenDown(t.ProgramId!.Value, t.TenantId, t.Subject, t.CoachSlug, t.Since!.Value))]));
    }

    // 1. catalog-api unpublishes and blocks the program first, with no transaction held open here; if it
    //    fails, nothing has changed (502).
    // 2. Then, in one transaction: the program's reports close, the coach's owners are emailed the reason,
    //    and the audit trail records it.
    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> UnpublishAsync(
        Guid programId, Decision request, TenantContext tenant, TenancyDbContext db, IReportedPrograms programs, IConfiguration configuration,
        TimeProvider time, CancellationToken cancellationToken)
    {
        if (AdminActions.Valid(request.Reason) is not { } reason)
        {
            return AdminActions.MissingReason();
        }

        ContentReport? report;
        Tenant? workspace;
        await using (var read = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            report = await db.ContentReports.AsNoTracking()
                .Where(r => r.Kind == ContentReportKind.Program && r.ProgramId == programId && r.ResolvedAt == null)
                .OrderBy(r => r.ReportedAt)
                .FirstOrDefaultAsync(cancellationToken);
            workspace = report is null ? null : await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == report.TenantId, cancellationToken);
        }

        if (report is null || workspace is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            if (!await programs.BlockAsync(report.TenantId, programId, blocked: true, cancellationToken))
            {
                return TypedResults.NotFound();
            }
        }
        catch (HttpRequestException)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The program could not be taken down yet. Nothing has changed; try again.");
        }

        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.ContentReports
            .Where(r => r.Kind == ContentReportKind.Program && r.ProgramId == programId && r.ResolvedAt == null)
            .ExecuteUpdateAsync(r => r.SetProperty(x => x.ResolvedAt, now).SetProperty(x => x.Outcome, ContentReportOutcome.Unpublished), cancellationToken);
        WorkspaceEmails.ToOwners(db, EmailTemplates.ProgramUnpublished, workspace, configuration, new()
        {
            ["programTitle"] = report.Subject,
            ["reason"] = reason,
        });
        db.AdminActions.Add(Action(tenant, AdminActionKind.ProgramUnpublished, report.TenantId, $"{report.Subject} (/c/{report.CoachSlug})", reason, now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    // A program taken down is allowed back: catalog-api lifts the block, and the coach may publish it again.
    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> AllowAsync(
        Guid programId, Decision request, TenantContext tenant, TenancyDbContext db, IReportedPrograms programs, TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (AdminActions.Valid(request.Reason) is not { } reason)
        {
            return AdminActions.MissingReason();
        }

        ContentReport? takenDown;
        await using (var read = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            takenDown = await db.ContentReports.AsNoTracking()
                .Where(r => r.Kind == ContentReportKind.Program && r.ProgramId == programId && r.Outcome == ContentReportOutcome.Unpublished)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (takenDown is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            await programs.BlockAsync(takenDown.TenantId, programId, blocked: false, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The program could not be allowed back yet. Nothing has changed; try again.");
        }

        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.ContentReports
            .Where(r => r.Kind == ContentReportKind.Program && r.ProgramId == programId && r.Outcome == ContentReportOutcome.Unpublished)
            .ExecuteUpdateAsync(r => r.SetProperty(x => x.Outcome, ContentReportOutcome.Allowed), cancellationToken);
        db.AdminActions.Add(Action(tenant, AdminActionKind.ProgramAllowed, takenDown.TenantId, $"{takenDown.Subject} (/c/{takenDown.CoachSlug})", reason, now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> KeepAsync(
        System.Linq.Expressions.Expression<Func<ContentReport, bool>> target, Decision request, TenantContext tenant, TenancyDbContext db, TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (AdminActions.Valid(request.Reason) is not { } reason)
        {
            return AdminActions.MissingReason();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var report = await db.ContentReports.AsNoTracking().Where(target).Where(r => r.ResolvedAt == null).FirstOrDefaultAsync(cancellationToken);
        if (report is null)
        {
            return TypedResults.NotFound();
        }

        var now = time.GetUtcNow();
        await db.ContentReports.Where(target).Where(r => r.ResolvedAt == null)
            .ExecuteUpdateAsync(r => r.SetProperty(x => x.ResolvedAt, now).SetProperty(x => x.Outcome, ContentReportOutcome.Kept), cancellationToken);
        db.AdminActions.Add(Action(tenant, AdminActionKind.ReportsKept, report.TenantId,
            report.Kind == ContentReportKind.Program ? $"{report.Subject} (/c/{report.CoachSlug})" : $"{report.Subject} (/c/{report.CoachSlug}), the coach", reason, now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static AdminAction Action(TenantContext tenant, AdminActionKind kind, Guid tenantId, string subject, string reason, DateTimeOffset at) => new()
    {
        AdminId = tenant.UserId ?? Guid.Empty,
        Action = kind,
        TenantId = tenantId,
        Subject = subject,
        Reason = reason,
        At = at,
    };
}

public sealed record ProgramOnSale(Guid ProgramId, Guid TenantId, string CoachSlug, string Title);

// catalog-api's side of a report, through its signed internal endpoints.
public interface IReportedPrograms
{
    // A program on sale (published, its coach not suspended), or null.
    Task<ProgramOnSale?> FindOnSaleAsync(Guid programId, CancellationToken cancellationToken);

    // Takes the program off the marketplace and blocks it, or lifts the block. False if the tenant has no such program.
    Task<bool> BlockAsync(Guid tenantId, Guid programId, bool blocked, CancellationToken cancellationToken);
}

public sealed class ServiceReportedPrograms(IHttpClientFactory clients) : IReportedPrograms
{
    public const string Catalog = "reports-catalog";

    public async Task<ProgramOnSale?> FindOnSaleAsync(Guid programId, CancellationToken cancellationToken)
    {
        using var response = await clients.CreateClient(Catalog).GetAsync($"/internal/programs/{programId}/offer", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ProgramOnSale>(cancellationToken);
    }

    public async Task<bool> BlockAsync(Guid tenantId, Guid programId, bool blocked, CancellationToken cancellationToken)
    {
        using var response = await clients.CreateClient(Catalog)
            .PutAsJsonAsync($"/internal/tenants/{tenantId}/programs/{programId}/block", new { blocked }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }
}
