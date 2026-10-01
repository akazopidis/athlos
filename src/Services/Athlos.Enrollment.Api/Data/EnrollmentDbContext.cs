using Athlos.Tenancy;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Enrollment.Api.Data;

public sealed class EnrollmentDbContext(DbContextOptions<EnrollmentDbContext> options, ITenantContext tenant)
    : TenantDbContext(options, tenant)
{
    public DbSet<ProgramEnrollment> Enrollments => Set<ProgramEnrollment>();

    public DbSet<WorkoutProgress> WorkoutProgress => Set<WorkoutProgress>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProgramEnrollment>(enrollment =>
        {
            enrollment.HasIndex(e => e.OrderId).IsUnique();
            // One enrollment per customer and program, even if two checkouts were both paid.
            enrollment.HasIndex(e => new { e.CustomerId, e.ProgramId }).IsUnique();
            enrollment.HasIndex(e => new { e.TenantId, e.ProgramId });
            // Lets progress rows point at (id, tenant, customer) together; see WorkoutProgress below.
            enrollment.HasAlternateKey(e => new { e.Id, e.TenantId, e.CustomerId });
            enrollment.Property(e => e.ProgramTitle).HasMaxLength(120);
            enrollment.Property(e => e.CoachSlug).HasMaxLength(40);
        });

        modelBuilder.Entity<WorkoutProgress>(progress =>
        {
            progress.ToTable("workout_progress");
            progress.HasIndex(p => new { p.EnrollmentId, p.WorkoutId }).IsUnique();
            progress.HasIndex(p => p.CustomerId);
            // The database itself guarantees a progress row matches its enrollment's tenant and customer.
            progress.HasOne<ProgramEnrollment>()
                .WithMany()
                .HasForeignKey(p => new { p.EnrollmentId, p.TenantId, p.CustomerId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId, e.CustomerId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        base.OnModelCreating(modelBuilder);
    }
}
