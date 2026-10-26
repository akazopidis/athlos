using System.Text.Json;
using Athlos.Enrollment.Api.Data;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Enrollment.Api.Features.MyPrograms;

// What a customer reads of a program they bought (design §2.1, §3.4): catalog-api's current content,
// or, once the coach deleted their workspace, the buyers' copy kept here. CoachLeft says which.
public static class PaidContent
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record Found(ProgramContentResponse Content, bool CoachLeft);

    // The copy is read inside the enrollment's tenant, in a scope of its own. No database connection is
    // held during the call to catalog-api.
    public static async Task<Found?> GetAsync(
        ProgramEnrollment enrollment, IProgramContent programs, IServiceScopeFactory scopes, CancellationToken cancellationToken)
    {
        if (await programs.GetAsync(enrollment.TenantId, enrollment.ProgramId, cancellationToken) is { } current)
        {
            return new Found(current, CoachLeft: false);
        }

        ProgramCopy? copy = null;
        await scopes.InTenantAsync<EnrollmentDbContext>(enrollment.TenantId, async db =>
            copy = await db.ProgramCopies.AsNoTracking().SingleOrDefaultAsync(c => c.ProgramId == enrollment.ProgramId, cancellationToken), cancellationToken);
        return copy is null ? null : new Found(ToContent(copy), CoachLeft: true);
    }

    public static string WorkoutsJson(List<ProgramContentWorkout> workouts) => JsonSerializer.Serialize(workouts, Json);

    private static ProgramContentResponse ToContent(ProgramCopy copy) => new(
        copy.ProgramId, copy.CoachSlug, copy.Title, copy.Description, copy.Category, copy.DurationWeeks, CoverImageUrl: null,
        JsonSerializer.Deserialize<List<ProgramContentWorkout>>(copy.Workouts, Json) ?? []);
}
