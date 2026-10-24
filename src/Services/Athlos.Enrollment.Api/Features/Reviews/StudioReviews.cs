using System.ComponentModel.DataAnnotations;
using Athlos.Enrollment.Api.Data;
using Athlos.Messaging;
using Athlos.Messaging.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Enrollment.Api.Features.Reviews;

// The coach's side of reviews (design §2.4): the workspace's reviews, newest first (optionally of one
// program), and one public reply per review, written, changed or removed by owners and staff. The
// coach cannot delete a customer's review. Tenant-scoped: the tenant filter and RLS show this tenant's
// reviews only, and the route's slug must match the enrollment's coach, as for sales, so another
// workspace's review is 404. The first reply emails the customer (review-replied), if their enrollment
// has an address.
public static class StudioReviews
{
    public sealed record Item(
        Guid Id, Guid ProgramId, string ProgramTitle, int Rating, string? Comment, string AuthorName, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
        string? Reply, DateTimeOffset? RepliedAt, DateTimeOffset? HiddenAt);

    // Average and ReviewCount: the workspace's reviews as the shop shows them (hidden ones left out).
    public sealed record Page(List<Item> Items, int PageNumber, int PageSize, int TotalCount, decimal Average, int ReviewCount);

    public sealed record Query(Guid? ProgramId, [property: Range(1, 10_000)] int Page = 1, [property: Range(1, ProgramReviews.MaxPageSize)] int PageSize = 20);

    public sealed record ReplyRequest([property: Required, MinLength(1), MaxLength(Review.MaxCommentLength)] string Text);

    public static void MapStudioReviews(this IEndpointRouteBuilder studio)
    {
        studio.MapGet("/reviews", List).WithName("ListStudioReviews").WithTags("Reviews");
        studio.MapPut("/reviews/{reviewId:guid}/reply", Reply).WithName("ReplyToReview").WithTags("Reviews");
        studio.MapDelete("/reviews/{reviewId:guid}/reply", RemoveReply).WithName("RemoveReviewReply").WithTags("Reviews");
    }

    private static async Task<Ok<Page>> List(string tenantSlug, [AsParameters] Query query, EnrollmentDbContext db, CancellationToken cancellationToken)
    {
        var reviews = Reviews(db, tenantSlug);
        if (query.ProgramId is { } programId)
        {
            reviews = reviews.Where(r => r.Review.ProgramId == programId);
        }

        var shown = Reviews(db, tenantSlug).Where(r => r.Review.HiddenAt == null);
        var reviewCount = await shown.CountAsync(cancellationToken);
        var average = reviewCount == 0 ? 0 : Math.Round(await shown.AverageAsync(r => (decimal)r.Review.Rating, cancellationToken), 2);
        return TypedResults.Ok(new Page(
            await reviews.OrderByDescending(r => r.Review.CreatedAt).ThenBy(r => r.Review.Id)
                .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
                .Select(r => new Item(r.Review.Id, r.Review.ProgramId, r.ProgramTitle, r.Review.Rating, r.Review.Comment, r.Review.AuthorName,
                    r.Review.CreatedAt, r.Review.UpdatedAt, r.Review.Reply, r.Review.RepliedAt, r.Review.HiddenAt))
                .ToListAsync(cancellationToken),
            query.Page, query.PageSize, await reviews.CountAsync(cancellationToken), average, reviewCount));
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> Reply(
        string tenantSlug, Guid reviewId, ReplyRequest request, EnrollmentDbContext db, TimeProvider time, IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var text = request.Text.Trim();
        if (text.Length == 0)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["text"] = ["Write a reply."] });
        }

        var found = await db.Reviews
            .Join(db.Enrollments.Where(e => e.CoachSlug == tenantSlug), r => r.EnrollmentId, e => e.Id, (r, e) => new { Review = r, Enrollment = e })
            .SingleOrDefaultAsync(x => x.Review.Id == reviewId, cancellationToken);
        if (found is null)
        {
            return TypedResults.NotFound();
        }

        if (found.Review.Reply is null && found.Enrollment.CustomerEmail is { } email)
        {
            var publicUrl = new Uri(configuration["App:PublicUrl"] ?? throw new InvalidOperationException("App:PublicUrl is not configured."));
            db.OutboxMessages.Add(OutboxMessage.For(EmailNotification.Queue, new EmailNotification(
                EmailTemplates.ReviewReplied, To: [email], ToOwnersOfTenant: null, Locale: "en", new Dictionary<string, string>
                {
                    ["programTitle"] = found.Enrollment.ProgramTitle,
                    ["coach"] = found.Enrollment.CoachSlug,
                    ["reply"] = text,
                    ["programUrl"] = new Uri(publicUrl, $"/me/programs/{found.Enrollment.ProgramId}").ToString(),
                })));
        }

        found.Review.Reply = text;
        found.Review.RepliedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> RemoveReply(
        string tenantSlug, Guid reviewId, EnrollmentDbContext db, CancellationToken cancellationToken)
    {
        var ids = Reviews(db, tenantSlug).Select(s => s.Review.Id);
        var changed = await db.Reviews
            .Where(r => r.Id == reviewId && ids.Contains(r.Id))
            .ExecuteUpdateAsync(r => r.SetProperty(x => x.Reply, (string?)null).SetProperty(x => x.RepliedAt, (DateTimeOffset?)null), cancellationToken);
        return changed == 0 ? TypedResults.NotFound() : TypedResults.NoContent();
    }

    private sealed class Row
    {
        public required Review Review { get; init; }

        public required string ProgramTitle { get; init; }
    }

    // This tenant's reviews (the filter and RLS), of enrollments sold under the route's slug.
    private static IQueryable<Row> Reviews(EnrollmentDbContext db, string tenantSlug) =>
        db.Reviews.AsNoTracking()
            .Join(db.Enrollments.Where(e => e.CoachSlug == tenantSlug), r => r.EnrollmentId, e => e.Id, (r, e) => new Row { Review = r, ProgramTitle = e.ProgramTitle });
}
