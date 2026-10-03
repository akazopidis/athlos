using System.Net;
using System.Net.Http.Json;

namespace Athlos.Enrollment.Api.Features.MyPrograms;

// The paid content of a program, from catalog-api (design §2.1: sync calls only where needed;
// enrollment never reads catalog's database). A signed service-to-service call; the tenant comes
// from the customer's own enrollment, never from the customer's request.
public interface IProgramContent
{
    // Null if the program no longer exists.
    Task<ProgramContentResponse?> GetAsync(Guid tenantId, Guid programId, CancellationToken cancellationToken);
}

public sealed record ProgramContentResponse(
    Guid ProgramId, string CoachSlug, string Title, string Description, string Level, int DurationWeeks, string? CoverImageUrl, List<ProgramContentWorkout> Workouts);

public sealed record ProgramContentWorkout(Guid Id, int Week, int Day, string Title, string Content, string? VideoUrl);

public sealed class CatalogProgramContent(HttpClient http) : IProgramContent
{
    public async Task<ProgramContentResponse?> GetAsync(Guid tenantId, Guid programId, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"/internal/tenants/{tenantId}/programs/{programId}/content", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ProgramContentResponse>(cancellationToken);
    }
}
