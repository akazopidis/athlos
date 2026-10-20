using System.Net.Http.Json;
using Athlos.Enrollment.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Enrollment.Api.Features.Reviews;

// A program's rating in the shop (design §2.4): product cards, sorting and the program page read it
// from catalog-api, which keeps a copy on the program. After every change to a program's reviews,
// inside the program's tenant and before the change commits:
//   1. LockAsync orders concurrent changes to the same program (a transaction-level advisory lock),
//      so the last copy sent counts every committed review;
//   2. PublishAsync counts the program's reviews and sends the count and average to catalog. The values
//      are absolute, so a repeated call is harmless; if catalog cannot be reached the caller lets the
//      change fail (a 502, or a retried message), so the copy never drifts for long.
public static class ProgramRatings
{
    public static Task LockAsync(EnrollmentDbContext db, Guid programId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({programId.ToString()}, 0))", cancellationToken);

    public static async Task PublishAsync(EnrollmentDbContext db, IProgramRatings ratings, Guid tenantId, Guid programId, CancellationToken cancellationToken)
    {
        var stats = await db.Reviews.AsNoTracking()
            .Where(r => r.ProgramId == programId && r.HiddenAt == null)
            .GroupBy(r => 1)
            .Select(g => new { Count = g.Count(), Average = g.Average(r => (decimal)r.Rating) })
            .SingleOrDefaultAsync(cancellationToken);
        await ratings.SetAsync(tenantId, programId, stats?.Count ?? 0, Math.Round(stats?.Average ?? 0, 2), cancellationToken);
    }
}

// catalog-api's PUT /internal/tenants/{tenantId}/programs/{programId}/rating (signed).
public interface IProgramRatings
{
    Task SetAsync(Guid tenantId, Guid programId, int count, decimal average, CancellationToken cancellationToken);
}

public sealed class CatalogProgramRatings(HttpClient http) : IProgramRatings
{
    public async Task SetAsync(Guid tenantId, Guid programId, int count, decimal average, CancellationToken cancellationToken)
    {
        using var response = await http.PutAsJsonAsync($"/internal/tenants/{tenantId}/programs/{programId}/rating", new { count, average }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
