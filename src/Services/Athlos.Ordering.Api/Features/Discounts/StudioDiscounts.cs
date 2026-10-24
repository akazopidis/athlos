using System.ComponentModel.DataAnnotations;
using Athlos.Ordering.Api.Data;
using Athlos.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Athlos.Ordering.Api.Features.Discounts;

// The workspace's discount codes in the studio (design §2.4): list them with their paid uses, create,
// change (everything but the code itself) and delete. Owners and staff, like prices. Tenant-scoped:
// the tenant filter and RLS show and change this tenant's codes only, so another workspace's code is
// 404. Orders keep the code's text, so deleting a code leaves past sales as they were. One code may be
// shown on the workspace's programs (IsPublic, without a use limit); every change sends the shown code
// to catalog-api (PublicDiscounts).
public static class StudioDiscounts
{
    public const int MaxCodes = 50;

    public sealed record Item(
        Guid Id, string Code, int PercentOff, Guid? ProgramId, DateTimeOffset? ExpiresAt, int? MaxRedemptions, bool IsActive, bool IsPublic, int Redemptions,
        DateTimeOffset CreatedAt);

    public sealed record CreateRequest(
        [property: Required, RegularExpression("^[A-Za-z0-9-]{3,20}$")] string Code,
        [property: Range(5, 90)] int PercentOff,
        Guid? ProgramId,
        DateTimeOffset? ExpiresAt,
        [property: Range(1, 100_000)] int? MaxRedemptions,
        bool IsPublic = false);

    public sealed record UpdateRequest(
        [property: Range(5, 90)] int PercentOff,
        Guid? ProgramId,
        DateTimeOffset? ExpiresAt,
        [property: Range(1, 100_000)] int? MaxRedemptions,
        bool IsActive,
        bool IsPublic = false);

    private const string PublicWithLimit = "A code shown on your programs is for everyone, so it cannot have a use limit.";

    public static void MapStudioDiscounts(this IEndpointRouteBuilder studio)
    {
        var discounts = studio.MapGroup("/discounts").WithTags("Discounts");
        discounts.MapGet("/", List).WithName("ListDiscountCodes");
        discounts.MapPost("/", Create).WithName("CreateDiscountCode");
        discounts.MapPut("/{discountId:guid}", Update).WithName("UpdateDiscountCode");
        discounts.MapDelete("/{discountId:guid}", Delete).WithName("DeleteDiscountCode");
    }

    private static async Task<Ok<List<Item>>> List(OrderingDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await Items(db, db.DiscountCodes.OrderByDescending(d => d.CreatedAt)).ToListAsync(cancellationToken));

    private static async Task<Results<Created<Item>, ValidationProblem, ProblemHttpResult>> Create(
        CreateRequest request, ITenantContext tenant, OrderingDbContext db, IPublicDiscounts shop, TimeProvider time, CancellationToken cancellationToken)
    {
        if (request.ExpiresAt is { } expires && expires <= time.GetUtcNow())
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["expiresAt"] = ["The end date must be in the future."] });
        }

        if (request.IsPublic && request.MaxRedemptions is not null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["isPublic"] = [PublicWithLimit] });
        }

        if (await db.DiscountCodes.CountAsync(cancellationToken) >= MaxCodes)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: $"A workspace can have up to {MaxCodes} discount codes. Delete one to add another.");
        }

        var code = new DiscountCode
        {
            Code = DiscountRules.Normalize(request.Code),
            PercentOff = request.PercentOff,
            ProgramId = request.ProgramId,
            ExpiresAt = request.ExpiresAt,
            MaxRedemptions = request.MaxRedemptions,
            IsPublic = request.IsPublic,
        };
        if (code.IsPublic)
        {
            await StopShowingOthersAsync(db, code.Id, cancellationToken);
        }

        db.DiscountCodes.Add(code);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: $"You already have the code {code.Code}.");
        }

        if (await PublishAsync(db, shop, tenant, time, cancellationToken) is { } failed)
        {
            return failed;
        }

        var item = await Items(db, db.DiscountCodes.Where(d => d.Id == code.Id)).SingleAsync(cancellationToken);
        return TypedResults.Created($"discounts/{code.Id}", item);
    }

    private static async Task<Results<Ok<Item>, NotFound, ValidationProblem, ProblemHttpResult>> Update(
        Guid discountId, UpdateRequest request, ITenantContext tenant, OrderingDbContext db, IPublicDiscounts shop, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var code = await db.DiscountCodes.SingleOrDefaultAsync(d => d.Id == discountId, cancellationToken);
        if (code is null)
        {
            return TypedResults.NotFound();
        }

        if (request.ExpiresAt is { } expires && expires <= time.GetUtcNow() && expires != code.ExpiresAt)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["expiresAt"] = ["The end date must be in the future."] });
        }

        if (request.IsPublic && request.MaxRedemptions is not null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["isPublic"] = [PublicWithLimit] });
        }

        if (request.IsPublic && !code.IsPublic)
        {
            await StopShowingOthersAsync(db, code.Id, cancellationToken);
        }

        code.PercentOff = request.PercentOff;
        code.ProgramId = request.ProgramId;
        code.ExpiresAt = request.ExpiresAt;
        code.MaxRedemptions = request.MaxRedemptions;
        code.IsActive = request.IsActive;
        code.IsPublic = request.IsPublic;
        await db.SaveChangesAsync(cancellationToken);
        if (await PublishAsync(db, shop, tenant, time, cancellationToken) is { } failed)
        {
            return failed;
        }

        return TypedResults.Ok(await Items(db, db.DiscountCodes.Where(d => d.Id == code.Id)).SingleAsync(cancellationToken));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> Delete(
        Guid discountId, ITenantContext tenant, OrderingDbContext db, IPublicDiscounts shop, TimeProvider time, CancellationToken cancellationToken)
    {
        if (await db.DiscountCodes.Where(d => d.Id == discountId).ExecuteDeleteAsync(cancellationToken) == 0)
        {
            return TypedResults.NotFound();
        }

        return await PublishAsync(db, shop, tenant, time, cancellationToken) is { } failed ? failed : TypedResults.NoContent();
    }

    // One code is shown at a time: showing another takes the earlier one off the programs.
    private static Task<int> StopShowingOthersAsync(OrderingDbContext db, Guid shownId, CancellationToken cancellationToken) =>
        db.DiscountCodes.Where(d => d.IsPublic && d.Id != shownId).ExecuteUpdateAsync(d => d.SetProperty(x => x.IsPublic, false), cancellationToken);

    // The shop's copy must follow, or the change is undone (an error status leaves the transaction uncommitted).
    private static async Task<ProblemHttpResult?> PublishAsync(
        OrderingDbContext db, IPublicDiscounts shop, ITenantContext tenant, TimeProvider time, CancellationToken cancellationToken)
    {
        try
        {
            await PublicDiscounts.PublishAsync(db, shop, tenant.TenantId!.Value, time.GetUtcNow(), cancellationToken);
            return null;
        }
        catch (HttpRequestException)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The shop could not be updated just now. Try again in a moment.");
        }
    }

    // The codes as the studio shows them, with their paid uses.
    private static IQueryable<Item> Items(OrderingDbContext db, IQueryable<DiscountCode> codes) =>
        codes.AsNoTracking().Select(d => new Item(
            d.Id, d.Code, d.PercentOff, d.ProgramId, d.ExpiresAt, d.MaxRedemptions, d.IsActive, d.IsPublic,
            db.Orders.Count(o => o.DiscountCode == d.Code && o.Status == OrderStatus.Paid),
            d.CreatedAt));
}
