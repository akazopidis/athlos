using Athlos.Catalog.Api.Data;
using Athlos.Catalog.Api.Features.Marketplace;
using Athlos.Media;
using Athlos.Tenancy;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Athlos.Catalog.Api.Features.Saved;

// The customer's saved programs (design §2.4, the wishlist). Saved rows are the customer's own (RLS:
// customer_id = app.user_id); the programs are read like the shop's.
// CROSS-TENANT SLICE (design §3.2, "a customer's saved programs"): runs in the public-read scope and
// ignores the tenant filter on programs, keeping published programs of active coaches only, as
// SearchPrograms does; RLS enforces the same, so a saved draft can never be read.
public static class SavedPrograms
{
    public const int MaxSaved = 100;

    public static void MapSavedPrograms(this IEndpointRouteBuilder me)
    {
        me.MapGet("/saved", List)
            .WithName("ListSavedPrograms")
            .WithTags("Saved programs");
        me.MapPut("/saved/{programId:guid}", Save)
            .WithName("SaveProgram")
            .WithTags("Saved programs");
        me.MapDelete("/saved/{programId:guid}", Unsave)
            .WithName("UnsaveProgram")
            .WithTags("Saved programs");
    }

    // Newest saved first; a program that left the shop is not listed (it returns if published again).
    private static async Task<Ok<List<SearchPrograms.Item>>> List(ITenantContext tenant, CatalogDbContext db, MediaStore media, CancellationToken cancellationToken)
    {
        var rows = await Own(db, tenant.UserId)
            .Join(OnSale(db), s => s.ProgramId, p => p.Id, (s, p) => new { s.SavedAt, Program = p })
            .OrderByDescending(r => r.SavedAt)
            .Select(r => new { r.Program.Id, r.Program.CoachSlug, r.Program.Slug, r.Program.Title, r.Program.Category, r.Program.DurationWeeks,
                r.Program.PriceAmount, r.Program.Currency, r.Program.CoverImagePath })
            .ToListAsync(cancellationToken);

        var items = new List<SearchPrograms.Item>(rows.Count);
        foreach (var p in rows)
        {
            items.Add(new SearchPrograms.Item(p.Id, p.CoachSlug, p.Slug, p.Title, p.Category, p.DurationWeeks, p.PriceAmount, p.Currency,
                await media.ReadLinkAsync(p.CoverImagePath, cancellationToken)));
        }

        return TypedResults.Ok(items);
    }

    // Idempotent: saving a saved program again is a 204 too. Only a program on sale can be saved.
    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> Save(
        Guid programId, ITenantContext tenant, CatalogDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (tenant.UserId is not { } customerId || !await OnSale(db).AnyAsync(p => p.Id == programId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var saved = Own(db, customerId);
        if (await saved.AnyAsync(s => s.ProgramId == programId, cancellationToken))
        {
            return TypedResults.NoContent();
        }

        if (await saved.CountAsync(cancellationToken) >= MaxSaved)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: $"You can save up to {MaxSaved} programs. Remove one to save another.");
        }

        db.SavedPrograms.Add(new SavedProgram { CustomerId = customerId, ProgramId = programId, SavedAt = time.GetUtcNow() });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Saved by a request a moment earlier: the outcome is the same. The transaction is aborted,
            // so answer with a status that does not commit it.
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "This program was just saved.");
        }

        return TypedResults.NoContent();
    }

    private static async Task<NoContent> Unsave(Guid programId, ITenantContext tenant, CatalogDbContext db, CancellationToken cancellationToken)
    {
        await db.SavedPrograms
            .Where(s => s.CustomerId == tenant.UserId && s.ProgramId == programId)
            .ExecuteDeleteAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static IQueryable<SavedProgram> Own(CatalogDbContext db, Guid? customerId) =>
        db.SavedPrograms.AsNoTracking().Where(s => customerId != null && s.CustomerId == customerId);

    private static IQueryable<TrainingProgram> OnSale(CatalogDbContext db) =>
        db.Programs.AsNoTracking()
            .IgnoreQueryFilters([TenantDbContext.TenantFilter])
            .Where(p => p.IsPublished && !p.TenantSuspended);
}
