using Athlos.Catalog.Api.Data;
using Athlos.Catalog.Api.Features.Marketplace;
using Athlos.Media;
using Athlos.Tenancy;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

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
    private static async Task<Ok<List<SearchPrograms.Item>>> List(
        ITenantContext tenant, CatalogDbContext db, MediaStore media, TimeProvider time, CancellationToken cancellationToken)
    {
        var rows = await Own(db, tenant.UserId)
            .Join(OnSale(db), s => s.ProgramId, p => p.Id, (s, p) => new { s.SavedAt, Program = p })
            .OrderByDescending(r => r.SavedAt)
            .Select(r => new { r.Program.Id, r.Program.TenantId, r.Program.CoachSlug, r.Program.Slug, r.Program.Title, r.Program.Category, r.Program.DurationWeeks,
                r.Program.PriceAmount, r.Program.Currency, CoverImagePath = r.Program.CoverCardPath ?? r.Program.CoverImagePath, r.Program.RatingAverage, r.Program.RatingCount })
            .ToListAsync(cancellationToken);

        // The codes their coaches show, as on the shop's cards (public; RLS public-read).
        var now = time.GetUtcNow();
        var tenantIds = rows.Select(p => p.TenantId).Distinct().ToList();
        var discounts = await db.PublicDiscounts.AsNoTracking()
            .IgnoreQueryFilters([TenantDbContext.TenantFilter])
            .Where(d => tenantIds.Contains(d.TenantId) && (d.ExpiresAt == null || d.ExpiresAt > now))
            .ToDictionaryAsync(d => d.TenantId, cancellationToken);

        var items = new List<SearchPrograms.Item>(rows.Count);
        foreach (var p in rows)
        {
            items.Add(new SearchPrograms.Item(p.Id, p.CoachSlug, p.Slug, p.Title, p.Category, p.DurationWeeks, p.PriceAmount, p.Currency,
                await media.ReadLinkAsync(p.CoverImagePath, cancellationToken), p.RatingAverage, p.RatingCount,
                DiscountOffer.For(p.Id, discounts.GetValueOrDefault(p.TenantId))));
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

        // ON CONFLICT DO NOTHING: two saves at the same moment (a double tap) both succeed, and one row
        // is kept. RLS checks the insert like any other (customer_id = app.user_id).
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO saved_programs (id, customer_id, program_id, saved_at)
            VALUES ({Guid.CreateVersion7()}, {customerId}, {programId}, {time.GetUtcNow()})
            ON CONFLICT (customer_id, program_id) DO NOTHING
            """, cancellationToken);
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
