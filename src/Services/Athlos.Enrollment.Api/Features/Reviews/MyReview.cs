using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Athlos.Enrollment.Api.Data;
using Athlos.Enrollment.Api.Features.MyPrograms;
using Athlos.Messaging;
using Athlos.Messaging.Contracts;
using Athlos.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Enrollment.Api.Features.Reviews;

// PUT and DELETE /api/v1/me/programs/{programId}/review (design §2.4): the customer reviews a program
// they bought, edits the review, or deletes it. Not enrolled: 404. As with progress, the customer's
// enrollment is found first (their own rows), then the write enters the enrollment's tenant, so RLS
// checks it like any tenant write and the composite foreign key ties it to that enrollment.
// A first review emails the workspace's owners (review-received).
public static class MyReview
{
    public sealed record Request([property: Range(1, 5)] int Rating, [property: MaxLength(Review.MaxCommentLength)] string? Comment);

    // HiddenAt: the platform admin hid it after a report; it no longer shows on the program's page.
    public sealed record Response(
        int Rating, string? Comment, string AuthorName, string? Reply, DateTimeOffset? RepliedAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? HiddenAt);

    public static Response ToResponse(Review r) => new(r.Rating, r.Comment, r.AuthorName, r.Reply, r.RepliedAt, r.CreatedAt, r.UpdatedAt, r.HiddenAt);

    public static void MapMyReview(this IEndpointRouteBuilder me)
    {
        var review = me.MapGroup("/programs/{programId:guid}/review").WithTags("My programs");
        review.MapPut("/", Write).WithName("WriteMyReview");
        review.MapDelete("/", Delete).WithName("DeleteMyReview");
    }

    private static async Task<Results<Ok<Response>, NotFound, ProblemHttpResult>> Write(
        Guid programId, Request request, ClaimsPrincipal user, TenantContext tenant, EnrollmentDbContext db, IProgramRatings ratings,
        IConfiguration configuration, TimeProvider time, CancellationToken cancellationToken)
    {
        if (await FindOwnAsync(tenant.UserId, db, programId, cancellationToken) is not { } enrollment)
        {
            return TypedResults.NotFound();
        }

        tenant.EnterTenant(enrollment.TenantId, role: null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await ProgramRatings.LockAsync(db, programId, cancellationToken);

        var review = await db.Reviews.SingleOrDefaultAsync(r => r.EnrollmentId == enrollment.Id, cancellationToken);
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        // The automatic check (ReviewFilter); a review it holds back emails nobody.
        var heldBack = ReviewFilter.Check(comment, ReviewFilter.BlockedWords(configuration));
        if (review is null)
        {
            review = new Review
            {
                EnrollmentId = enrollment.Id,
                CustomerId = enrollment.CustomerId,
                ProgramId = programId,
                AuthorName = AuthorName(user),
            };
            db.Reviews.Add(review);
            if (heldBack is null)
            {
                db.OutboxMessages.Add(OutboxMessage.For(EmailNotification.Queue, ReviewEmail(enrollment, request.Rating, comment, configuration)));
            }
        }

        review.Rating = request.Rating;
        review.Comment = comment;
        if (heldBack is { } reason && review.HiddenAt is null)
        {
            await HoldBackAsync(db, review, enrollment, reason, time.GetUtcNow(), cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);

        if (await PublishAsync(db, ratings, enrollment, cancellationToken) is { } failed)
        {
            return failed;
        }

        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok(ToResponse(review));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> Delete(
        Guid programId, TenantContext tenant, EnrollmentDbContext db, IProgramRatings ratings, CancellationToken cancellationToken)
    {
        if (await FindOwnAsync(tenant.UserId, db, programId, cancellationToken) is not { } enrollment)
        {
            return TypedResults.NotFound();
        }

        tenant.EnterTenant(enrollment.TenantId, role: null);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await ProgramRatings.LockAsync(db, programId, cancellationToken);
        var removed = await db.Reviews
            .Where(r => r.EnrollmentId == enrollment.Id && r.CustomerId == enrollment.CustomerId)
            .ExecuteDeleteAsync(cancellationToken);

        if (removed > 0 && await PublishAsync(db, ratings, enrollment, cancellationToken) is { } failed)
        {
            return failed;
        }

        await transaction.CommitAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    // The shop's copy of the rating must follow, or the change is undone (the transaction is not committed).
    private static async Task<ProblemHttpResult?> PublishAsync(
        EnrollmentDbContext db, IProgramRatings ratings, ProgramEnrollment enrollment, CancellationToken cancellationToken)
    {
        try
        {
            await ProgramRatings.PublishAsync(db, ratings, enrollment.TenantId, enrollment.ProgramId, cancellationToken);
            return null;
        }
        catch (HttpRequestException)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Your review could not be saved just now. Try again in a moment.");
        }
    }

    // The automatic check caught the comment (ReviewFilter): the review is hidden, and waits in the admin's
    // queue as a report from Athlos. An earlier decision on the filter's report (the admin kept the old
    // text) is replaced by this one; a report still open is enough.
    private static async Task HoldBackAsync(
        EnrollmentDbContext db, Review review, ProgramEnrollment enrollment, ReportReason reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        review.HiddenAt = now;
        var earlier = await db.ReviewReports.SingleOrDefaultAsync(r => r.ReviewId == review.Id && r.ReporterId == ReviewFilter.ReporterId, cancellationToken);
        if (earlier is { ResolvedAt: null })
        {
            return;
        }

        if (earlier is not null)
        {
            db.ReviewReports.Remove(earlier);
        }

        db.ReviewReports.Add(new ReviewReport
        {
            ReviewId = review.Id,
            TenantId = enrollment.TenantId,
            ProgramId = enrollment.ProgramId,
            ProgramTitle = enrollment.ProgramTitle,
            CoachSlug = enrollment.CoachSlug,
            AuthorName = review.AuthorName,
            Rating = review.Rating,
            Comment = review.Comment,
            ReporterId = ReviewFilter.ReporterId,
            Reason = reason,
            Note = ReviewFilter.Note,
            ReportedAt = now,
        });
    }

    // "Maria Kostas" becomes "Maria K."; no name in the token, "Customer".
    public static string AuthorName(ClaimsPrincipal user)
    {
        var parts = (user.FindFirst("name")?.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var name = parts.Length switch
        {
            0 => "Customer",
            1 => parts[0],
            _ => $"{parts[0]} {char.ToUpperInvariant(parts[^1][0])}.",
        };
        return name.Length <= 60 ? name : name[..60];
    }

    // To the workspace's owners (the worker looks up their addresses, design §5.3).
    private static EmailNotification ReviewEmail(ProgramEnrollment enrollment, int rating, string? comment, IConfiguration configuration)
    {
        var publicUrl = new Uri(configuration["App:PublicUrl"] ?? throw new InvalidOperationException("App:PublicUrl is not configured."));
        return new EmailNotification(EmailTemplates.ReviewReceived, To: [], ToOwnersOfTenant: enrollment.TenantId, Locale: "en", new Dictionary<string, string>
        {
            ["programTitle"] = enrollment.ProgramTitle,
            ["stars"] = $"{new string('★', rating)}{new string('☆', 5 - rating)}",
            ["rating"] = rating.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["comment"] = comment ?? "(no comment)",
            ["reviewsUrl"] = new Uri(publicUrl, $"/studio/{enrollment.CoachSlug}/reviews").ToString(),
        });
    }

    private static async Task<ProgramEnrollment?> FindOwnAsync(Guid? userId, EnrollmentDbContext db, Guid programId, CancellationToken cancellationToken)
    {
        if (userId is not { } customerId)
        {
            return null;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var enrollment = await MyEnrollments.FindAsync(db, customerId, programId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return enrollment;
    }
}
