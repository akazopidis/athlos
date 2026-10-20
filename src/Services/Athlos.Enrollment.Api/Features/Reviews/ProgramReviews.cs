using System.ComponentModel.DataAnnotations;
using Athlos.Enrollment.Api.Data;
using Athlos.Enrollment.Api.Features.MyPrograms;
using Athlos.Tenancy;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Enrollment.Api.Features.Reviews;

// GET /api/v1/marketplace/programs/{programId}/reviews (design §2.4): a program's reviews on its
// public page, newest first, with the average, the count and how many gave each number of stars.
// CROSS-TENANT SLICE (design §3.2, "the public reviews of a program"): the visitor is in no tenant,
// so the read ignores the tenant filter and runs in the public-read scope, where RLS shows every
// review (and nothing else of the tenant's: enrollments and progress stay hidden). Reviewers are shown
// by the name they reviewed under; no customer id leaves.
// Reviews are public only while their program is on sale (catalog's offer, asked before the database is
// read, so no connection waits on the call), and reviews the platform admin hid are left out.
public static class ProgramReviews
{
    public const int MaxPageSize = 50;

    // Mine: the signed-in caller wrote it (they cannot report their own review); false for everyone else.
    public sealed record Item(
        Guid Id, int Rating, string? Comment, string AuthorName, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? Reply, DateTimeOffset? RepliedAt, bool Mine);

    // Stars[0] counts one-star reviews, Stars[4] five-star ones. API convention for paged lists (design §7.1).
    public sealed record Response(decimal Average, int Count, List<int> Stars, List<Item> Items, int Page, int PageSize, int TotalCount);

    public sealed record Paging([property: Range(1, 10_000)] int Page = 1, [property: Range(1, MaxPageSize)] int PageSize = 10);

    public static void MapProgramReviews(this IEndpointRouteBuilder marketplace) =>
        marketplace.MapGet("/programs/{programId:guid}/reviews", Handle)
            .WithName("ListProgramReviews")
            .WithTags("Reviews");

    private static async Task<Results<Ok<Response>, NotFound>> Handle(
        Guid programId, [AsParameters] Paging paging, ITenantContext tenant, EnrollmentDbContext db, IProgramContent programs, CancellationToken cancellationToken)
    {
        if (await programs.GetOnSaleAsync(programId, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }

        var userId = tenant.UserId;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var reviews = db.Reviews.AsNoTracking()
            .IgnoreQueryFilters([TenantDbContext.TenantFilter])
            .Where(r => r.ProgramId == programId && r.HiddenAt == null);

        var counts = await reviews.GroupBy(r => r.Rating).Select(g => new { Rating = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var stars = Enumerable.Range(1, 5).Select(n => counts.SingleOrDefault(c => c.Rating == n)?.Count ?? 0).ToList();
        var count = stars.Sum();
        var average = count == 0 ? 0 : Math.Round((decimal)counts.Sum(c => c.Rating * c.Count) / count, 2);

        var items = await reviews.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id)
            .Skip((paging.Page - 1) * paging.PageSize).Take(paging.PageSize)
            .Select(r => new Item(r.Id, r.Rating, r.Comment, r.AuthorName, r.CreatedAt, r.UpdatedAt, r.Reply, r.RepliedAt, userId != null && r.CustomerId == userId))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new Response(average, count, stars, items, paging.Page, paging.PageSize, count));
    }
}
