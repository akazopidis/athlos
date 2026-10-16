using Athlos.Tenancy;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Catalog.Api.Data;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options, ITenantContext tenant)
    : TenantDbContext(options, tenant)
{
    public DbSet<TrainingProgram> Programs => Set<TrainingProgram>();

    public DbSet<Workout> Workouts => Set<Workout>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TrainingProgram>(program =>
        {
            program.ToTable("programs");
            // Every index leads with tenant_id (design §4).
            program.HasIndex(p => new { p.TenantId, p.Slug }).IsUnique();
            // Marketplace: published programs, newest first.
            program.HasIndex(p => new { p.IsPublished, p.PublishedAt });
            program.Property(p => p.CoachSlug).HasMaxLength(40);
            program.Property(p => p.Slug).HasMaxLength(60);
            program.Property(p => p.Title).HasMaxLength(120);
            program.Property(p => p.Description).HasMaxLength(4000);
            program.Property(p => p.Category).HasConversion<string>().HasMaxLength(20);
            program.Property(p => p.Currency).HasMaxLength(3).IsFixedLength();
            program.Property(p => p.CoverImagePath).HasMaxLength(300);
        });

        modelBuilder.Entity<Workout>(workout =>
        {
            workout.HasIndex(w => new { w.TenantId, w.ProgramId, w.Week, w.Day });
            workout.Property(w => w.Title).HasMaxLength(120);
            workout.Property(w => w.Content).HasMaxLength(20000);
            workout.Property(w => w.VideoUrl).HasMaxLength(500);
            workout.HasOne<TrainingProgram>().WithMany().HasForeignKey(w => w.ProgramId).OnDelete(DeleteBehavior.Cascade);
        });

        base.OnModelCreating(modelBuilder);
    }
}
