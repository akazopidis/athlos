using Athlos.Tenancy.Data;

namespace Athlos.Enrollment.Api.Data;

// The buyers' copy of a program whose coach deleted their workspace (design §3.4): taken from
// catalog-api when the workspace is deleted, before catalog erases the program, so the customers who
// paid for it keep its content, read-only, with their progress. It stays in the old tenant, so the
// tenant filter and RLS apply to it as to the enrollments it serves. Removed once nobody owns the
// program any more (the last buyer deletes their account).
public sealed class ProgramCopy : ITenantOwned
{
    public required Guid ProgramId { get; init; }

    public Guid TenantId { get; set; }

    public required string CoachSlug { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required string Category { get; init; }

    public required int DurationWeeks { get; init; }

    // The workouts as catalog-api gave them (ProgramContentWorkout), as JSON.
    public required string Workouts { get; init; }

    public DateTimeOffset CopiedAt { get; init; }
}
