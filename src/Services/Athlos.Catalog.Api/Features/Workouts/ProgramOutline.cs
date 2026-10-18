using Athlos.Catalog.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Features.Workouts;

// Keeps a program's public outline and free preview (TrainingProgram.Outline, .Sample) in step with its
// workouts. Called inside the change's own transaction, before SaveChanges, wherever workouts change.
public static class ProgramOutline
{
    public static async Task RefreshAsync(CatalogDbContext db, Guid programId, CancellationToken cancellationToken)
    {
        var program = await db.Programs.SingleAsync(p => p.Id == programId, cancellationToken);
        // Saved workouts plus the ones added in this unit of work, minus the ones removed in it.
        var removed = db.ChangeTracker.Entries<Workout>().Where(e => e.State == EntityState.Deleted).Select(e => e.Entity.Id).ToHashSet();
        var saved = await db.Workouts.Where(w => w.ProgramId == programId).ToListAsync(cancellationToken);
        var added = db.ChangeTracker.Entries<Workout>().Where(e => e.State == EntityState.Added && e.Entity.ProgramId == programId).Select(e => e.Entity);
        var workouts = saved.Concat(added).DistinctBy(w => w.Id).Where(w => !removed.Contains(w.Id)).OrderBy(w => w.Week).ThenBy(w => w.Day).ToList();

        program.Outline = [.. workouts.Select(w => new OutlineEntry { Week = w.Week, Day = w.Day, Title = w.Title, Sample = w.IsSample })];
        program.Sample = workouts.FirstOrDefault(w => w.IsSample) is { } sample
            ? new SampleWorkout { Week = sample.Week, Day = sample.Day, Title = sample.Title, Content = sample.Content, VideoUrl = sample.VideoUrl }
            : null;
    }
}
