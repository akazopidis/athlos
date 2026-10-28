using Athlos.Tenancy;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace Athlos.Catalog.Api.Data;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options, ITenantContext tenant)
    : TenantDbContext(options, tenant)
{
    public DbSet<TrainingProgram> Programs => Set<TrainingProgram>();

    public DbSet<Workout> Workouts => Set<Workout>();

    public DbSet<SavedProgram> SavedPrograms => Set<SavedProgram>();

    public DbSet<PublicDiscount> PublicDiscounts => Set<PublicDiscount>();

    public DbSet<CoachName> CoachNames => Set<CoachName>();

    // The marketplace search's words (design §8): title first, then description, English stemming.
    // A shadow property, so program code never handles it; the database computes it.
    public const string SearchVector = "SearchVector";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Trigram matching for the search's typo tolerance (allowed on the server by azure.extensions, infra/modules/postgres.bicep).
        modelBuilder.HasPostgresExtension("pg_trgm");

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
            program.Property(p => p.RatingAverage).HasPrecision(3, 2);
            program.OwnsMany(p => p.Outline, outline => outline.ToJson());
            program.OwnsOne(p => p.Sample, sample => sample.ToJson());
            program.Property<NpgsqlTsVector>(SearchVector)
                .HasComputedColumnSql("setweight(to_tsvector('english', title), 'A') || setweight(to_tsvector('english', description), 'B')", stored: true);
            program.HasIndex(SearchVector).HasMethod("gin");
            program.HasIndex(p => p.Title).HasMethod("gin").HasOperators("gin_trgm_ops");
        });

        modelBuilder.Entity<Workout>(workout =>
        {
            workout.HasIndex(w => new { w.TenantId, w.ProgramId, w.Week, w.Day });
            workout.Property(w => w.Title).HasMaxLength(120);
            workout.Property(w => w.Content).HasMaxLength(20000);
            workout.Property(w => w.VideoUrl).HasMaxLength(500);
            workout.HasOne<TrainingProgram>().WithMany().HasForeignKey(w => w.ProgramId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SavedProgram>(saved =>
        {
            saved.ToTable("saved_programs");
            saved.HasIndex(s => new { s.CustomerId, s.ProgramId }).IsUnique();
            saved.HasOne<TrainingProgram>().WithMany().HasForeignKey(s => s.ProgramId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PublicDiscount>(discount =>
        {
            discount.ToTable("public_discounts");
            discount.HasKey(d => d.TenantId);
            discount.Property(d => d.Code).HasMaxLength(20);
        });

        modelBuilder.Entity<CoachName>(coach =>
        {
            coach.ToTable("coach_names");
            coach.HasKey(c => c.TenantId);
            coach.Property(c => c.DisplayName).HasMaxLength(100);
            coach.HasIndex(c => c.DisplayName).HasMethod("gin").HasOperators("gin_trgm_ops");
        });

        base.OnModelCreating(modelBuilder);
    }
}
