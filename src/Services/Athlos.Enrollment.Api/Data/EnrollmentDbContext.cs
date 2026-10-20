using Athlos.Messaging;
using Athlos.Tenancy;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Enrollment.Api.Data;

public sealed class EnrollmentDbContext(DbContextOptions<EnrollmentDbContext> options, ITenantContext tenant)
    : TenantDbContext(options, tenant), IHasOutbox
{
    public DbSet<ProgramEnrollment> Enrollments => Set<ProgramEnrollment>();

    public DbSet<WorkoutProgress> WorkoutProgress => Set<WorkoutProgress>();

    public DbSet<Review> Reviews => Set<Review>();

    public DbSet<ReviewReport> ReviewReports => Set<ReviewReport>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

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
            enrollment.Property(e => e.CustomerEmail).HasMaxLength(320);
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

        modelBuilder.Entity<Review>(review =>
        {
            review.HasIndex(r => r.EnrollmentId).IsUnique();
            // The program page and the studio list a program's reviews, newest first.
            review.HasIndex(r => new { r.TenantId, r.ProgramId, r.CreatedAt });
            review.HasIndex(r => r.ProgramId);
            review.HasIndex(r => r.CustomerId);
            review.Property(r => r.Comment).HasMaxLength(Review.MaxCommentLength);
            review.Property(r => r.Reply).HasMaxLength(Review.MaxCommentLength);
            review.Property(r => r.AuthorName).HasMaxLength(60);
            review.ToTable(t => t.HasCheckConstraint("ck_reviews_rating", "rating BETWEEN 1 AND 5"));
            // Like progress: the database ties a review to its enrollment's tenant and customer.
            review.HasOne<ProgramEnrollment>()
                .WithMany()
                .HasForeignKey(r => new { r.EnrollmentId, r.TenantId, r.CustomerId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId, e.CustomerId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReviewReport>(report =>
        {
            report.ToTable("review_reports");
            report.HasIndex(r => new { r.ReviewId, r.ReporterId }).IsUnique();
            report.HasIndex(r => r.ResolvedAt);
            report.Property(r => r.ProgramTitle).HasMaxLength(120);
            report.Property(r => r.CoachSlug).HasMaxLength(40);
            report.Property(r => r.AuthorName).HasMaxLength(60);
            report.Property(r => r.Comment).HasMaxLength(Review.MaxCommentLength);
            report.Property(r => r.Note).HasMaxLength(500);
            report.Property(r => r.Reason).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.ConfigureOutbox();

        base.OnModelCreating(modelBuilder);
    }
}
