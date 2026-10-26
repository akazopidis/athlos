using System.ComponentModel.DataAnnotations;
using Athlos.Enrollment.Api.Data;
using Athlos.Enrollment.Api.Features.MyPrograms;
using Athlos.Tenancy;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Athlos.Enrollment.Api.Features.Reviews;

// Moderating reviews (design §2.4). Anyone signed in (a customer, or the coach) reports a review on a
// program that is on sale; the platform admin sees open reports and hides the review or keeps it.
//
// - Reporting reads the review in the public-read scope (a listed cross-tenant slice, like the public
//   list) and copies what the admin needs into review_reports, a work queue of the service (allow-all
//   policy, like ordering's payout_retries), so the admin never reads a tenant's reviews.
// - Hiding or restoring enters the review's tenant, so the tenant filter and RLS confine the change to
//   that one review; the program's rating in the shop follows, as for any change to its reviews.
// - Every decision states a reason, recorded with the admin in admin_actions when the reports close.
public static class ReviewModeration
{
    public sealed record ReportRequest(ReportReason Reason, [property: MaxLength(500)] string? Note);

    public sealed record DecisionRequest(string? Reason);

    public sealed record OpenReport(
        Guid ReviewId, Guid ProgramId, string ProgramTitle, string CoachSlug, string AuthorName, int Rating, string? Comment,
        int Reports, List<ReportReason> Reasons, List<string> Notes, DateTimeOffset FirstReportedAt);

    public static void MapReportReview(this IEndpointRouteBuilder marketplace) =>
        marketplace.MapPost("/programs/{programId:guid}/reviews/{reviewId:guid}/report", Report)
            .RequireAuthorization()
            .WithName("ReportReview")
            .WithTags("Reviews");

    public static void MapAdminReviews(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/admin/reviews")
            .RequireAuthorization(PlatformAdminAuthorization.Policy)
            .WithTags("Admin");
        admin.MapGet("/reports", ListReports).WithName("AdminListReviewReports");
        admin.MapPost("/{reviewId:guid}/hide", (Guid reviewId, DecisionRequest request, TenantContext tenant, EnrollmentDbContext db, IServiceScopeFactory scopes,
                IProgramRatings ratings, TimeProvider time, CancellationToken cancellationToken) =>
                DecideAsync(reviewId, hide: true, request, tenant, db, scopes, ratings, time, cancellationToken))
            .WithName("AdminHideReview");
        admin.MapPost("/{reviewId:guid}/keep", (Guid reviewId, DecisionRequest request, TenantContext tenant, EnrollmentDbContext db, IServiceScopeFactory scopes,
                IProgramRatings ratings, TimeProvider time, CancellationToken cancellationToken) =>
                DecideAsync(reviewId, hide: false, request, tenant, db, scopes, ratings, time, cancellationToken))
            .WithName("AdminKeepReview");
        admin.MapAdminActions<EnrollmentDbContext>("/actions").WithName("AdminListReviewActions");
    }

    // 204 whether or not this person reported it before: one report per review and reporter.
    private static async Task<Results<NoContent, NotFound>> Report(
        Guid programId, Guid reviewId, ReportRequest request, TenantContext tenant, EnrollmentDbContext db, IProgramContent programs, TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (tenant.UserId is not { } reporter || await programs.GetOnSaleAsync(programId, cancellationToken) is not { } program)
        {
            return TypedResults.NotFound();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var review = await db.Reviews.AsNoTracking()
            .IgnoreQueryFilters([TenantDbContext.TenantFilter])
            .SingleOrDefaultAsync(r => r.Id == reviewId && r.ProgramId == programId && r.HiddenAt == null, cancellationToken);
        if (review is null)
        {
            return TypedResults.NotFound();
        }

        db.ReviewReports.Add(new ReviewReport
        {
            ReviewId = review.Id,
            TenantId = review.TenantId,
            ProgramId = review.ProgramId,
            ProgramTitle = program.Title,
            CoachSlug = program.CoachSlug,
            AuthorName = review.AuthorName,
            Rating = review.Rating,
            Comment = review.Comment,
            ReporterId = reporter,
            Reason = request.Reason,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            ReportedAt = time.GetUtcNow(),
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return TypedResults.NoContent(); // Reported before: nothing to add.
        }

        await transaction.CommitAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    // Open reports, one entry per review, oldest first.
    private static async Task<Ok<List<OpenReport>>> ListReports(EnrollmentDbContext db, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var open = await db.ReviewReports.AsNoTracking()
            .Where(r => r.ResolvedAt == null)
            .OrderBy(r => r.ReportedAt)
            .Take(500)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(open
            .GroupBy(r => r.ReviewId)
            .Select(g =>
            {
                var first = g.First();
                return new OpenReport(first.ReviewId, first.ProgramId, first.ProgramTitle, first.CoachSlug, first.AuthorName, first.Rating, first.Comment,
                    g.Count(), [.. g.Select(r => r.Reason).Distinct()], [.. g.Where(r => r.Note != null).Select(r => r.Note!)], first.ReportedAt);
            })
            .Take(100)
            .ToList());
    }

    // Hide (or keep) the review and close its reports. The review is changed inside its tenant, and the
    // program's rating follows before the change commits, as for any review change. A review already
    // gone (refunded, erased) just closes its reports.
    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DecideAsync(
        Guid reviewId, bool hide, DecisionRequest request, TenantContext tenant, EnrollmentDbContext db, IServiceScopeFactory scopes, IProgramRatings ratings,
        TimeProvider time, CancellationToken cancellationToken)
    {
        if (AdminActions.Valid(request.Reason) is not { } reason)
        {
            return AdminActions.MissingReason();
        }

        ReviewReport? report;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            report = await db.ReviewReports.AsNoTracking().Where(r => r.ReviewId == reviewId).OrderBy(r => r.ReportedAt).FirstOrDefaultAsync(cancellationToken);
        }

        if (report is null)
        {
            return TypedResults.NotFound();
        }

        var now = time.GetUtcNow();
        try
        {
            await scopes.InTenantAsync<EnrollmentDbContext>(report.TenantId, async tenantDb =>
            {
                await ProgramRatings.LockAsync(tenantDb, report.ProgramId, cancellationToken);
                var changed = await tenantDb.Reviews
                    .Where(r => r.Id == reviewId)
                    .ExecuteUpdateAsync(r => r.SetProperty(x => x.HiddenAt, hide ? now : null), cancellationToken);
                if (changed > 0)
                {
                    await ProgramRatings.PublishAsync(tenantDb, ratings, report.TenantId, report.ProgramId, cancellationToken);
                }
            }, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The program's rating could not be updated just now. Try again.");
        }

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await db.ReviewReports
                .Where(r => r.ReviewId == reviewId && r.ResolvedAt == null)
                .ExecuteUpdateAsync(r => r.SetProperty(x => x.ResolvedAt, now).SetProperty(x => x.Hidden, hide), cancellationToken);
            // The program, not the review's author or text: the log keeps no customer's personal data.
            db.AdminActions.Add(new AdminAction
            {
                AdminId = tenant.UserId ?? Guid.Empty,
                Action = hide ? AdminActionKind.ReviewHidden : AdminActionKind.ReviewKept,
                TenantId = report.TenantId,
                Subject = $"{report.ProgramTitle} (/c/{report.CoachSlug})",
                Detail = $"{report.Rating}-star review",
                Reason = reason,
                At = now,
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return TypedResults.NoContent();
    }
}
