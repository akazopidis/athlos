using System.Net.Http.Json;

namespace Athlos.Catalog.Api.Features.Workouts;

// Telling the customers of a program that it has a new workout (design §5.3). Catalog has no outbox,
// so it calls enrollment-api, which owns the customers and their addresses and queues the emails
// (at most one a day each). The call is made after the response, once the workout is committed, and
// is best effort: if enrollment-api cannot be reached the workout is saved anyway and the customers are
// not told about this one (logged). The same goes for a lower price on a saved program (v17.9): catalog
// names the customers who saved it, enrollment checks their choices and queues the emails.
public interface IProgramUpdates
{
    Task WorkoutAddedAsync(Guid tenantId, Guid programId, string workoutTitle, CancellationToken cancellationToken);

    Task PriceDroppedAsync(PriceDrop drop, CancellationToken cancellationToken);
}

public sealed record PriceDrop(
    Guid ProgramId, string ProgramTitle, string CoachSlug, string ProgramSlug, long OldPriceAmount, long NewPriceAmount, string Currency,
    IReadOnlyList<Guid> CustomerIds);

public sealed partial class EnrollmentProgramUpdates(HttpClient http, ILogger<EnrollmentProgramUpdates> logger) : IProgramUpdates
{
    public async Task WorkoutAddedAsync(Guid tenantId, Guid programId, string workoutTitle, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(
                $"/internal/tenants/{tenantId}/programs/{programId}/updated", new { workoutTitle }, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            LogNotTold(logger, programId, e);
        }
    }

    // In batches of enrollment-api's limit per call.
    public async Task PriceDroppedAsync(PriceDrop drop, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var batch in drop.CustomerIds.Chunk(500))
            {
                using var response = await http.PostAsJsonAsync("/internal/price-drops", new
                {
                    drop.ProgramTitle,
                    drop.CoachSlug,
                    drop.ProgramSlug,
                    drop.OldPriceAmount,
                    drop.NewPriceAmount,
                    drop.Currency,
                    CustomerIds = batch,
                }, cancellationToken);
                response.EnsureSuccessStatusCode();
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            LogPriceDropNotTold(logger, drop.ProgramId, e);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Customers who saved program {ProgramId} could not be told about its lower price")]
    private static partial void LogPriceDropNotTold(ILogger logger, Guid programId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Customers of program {ProgramId} could not be told about its new workout")]
    private static partial void LogNotTold(ILogger logger, Guid programId, Exception exception);
}
