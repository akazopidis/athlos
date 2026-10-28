using System.Text.RegularExpressions;
using Athlos.Tenancy.Api.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Internal;

// For the notifications-worker (signed service-to-service calls): addresses that bounce (design §5.3).
// The worker records one from Communication Services' delivery report and asks before it sends. Both
// sides name addresses by hash only, so this list holds no address. No tenant is entered: the table
// is the service's own (policy service_only).
public static partial class EmailSuppressions
{
    // A mailbox that bounced is skipped for half a year, then tried again; older rows are deleted.
    public const int Days = 180;

    private const int MaxHashesPerCheck = 50;

    private static readonly HashSet<string> Statuses = new(StringComparer.Ordinal) { "Bounced", "Suppressed" };

    public sealed record CheckResponse(List<string> Suppressed);

    public sealed record RecordRequest(string Status);

    public static void MapEmailSuppressions(this IEndpointRouteBuilder app)
    {
        app.MapGet("/internal/email-suppressions", Check).ExcludeFromDescription();
        app.MapPut("/internal/email-suppressions/{hash}", Record).ExcludeFromDescription();
    }

    // GET /internal/email-suppressions?hash=..&hash=..: which of these addresses to skip.
    private static async Task<Results<Ok<CheckResponse>, ValidationProblem>> Check(
        string[]? hash, TenancyDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var hashes = (hash ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (hashes.Count > MaxHashesPerCheck || hashes.Any(h => !IsHash(h)))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["hash"] = [$"Up to {MaxHashesPerCheck} lowercase hex SHA-256 hashes."],
            });
        }

        var since = time.GetUtcNow().AddDays(-Days);
        var suppressed = hashes.Count == 0
            ? []
            : await db.EmailSuppressions.AsNoTracking()
                .Where(s => hashes.Contains(s.AddressHash) && s.RecordedAt > since)
                .Select(s => s.AddressHash)
                .ToListAsync(cancellationToken);

        return TypedResults.Ok(new CheckResponse(suppressed));
    }

    // PUT /internal/email-suppressions/{hash}: an address bounced. Idempotent: a repeat refreshes it.
    private static async Task<Results<NoContent, ValidationProblem>> Record(
        string hash, RecordRequest request, TenancyDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (!IsHash(hash) || !Statuses.Contains(request.Status))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["hash"] = ["A lowercase hex SHA-256 hash."],
                ["status"] = [$"One of {string.Join(", ", Statuses)}."],
            });
        }

        var now = time.GetUtcNow();
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO email_suppressions (id, address_hash, status, recorded_at)
            VALUES ({Guid.CreateVersion7()}, {hash}, {request.Status}, {now})
            ON CONFLICT (address_hash) DO UPDATE SET status = excluded.status, recorded_at = excluded.recorded_at
            """, cancellationToken);
        var expired = now.AddDays(-Days);
        await db.EmailSuppressions.Where(s => s.RecordedAt <= expired).ExecuteDeleteAsync(cancellationToken);

        return TypedResults.NoContent();
    }

    private static bool IsHash(string value) => Sha256Hex().IsMatch(value);

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Hex();
}
