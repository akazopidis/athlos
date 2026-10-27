using Athlos.Messaging;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Data;

public sealed class TenancyDbContext(DbContextOptions<TenancyDbContext> options, ITenantContext tenant)
    : TenantDbContext(options, tenant), IHasOutbox, IHasAdminActions
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();

    public DbSet<StaffInvite> StaffInvites => Set<StaffInvite>();

    public DbSet<ContentReport> ContentReports => Set<ContentReport>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<AdminAction> AdminActions => Set<AdminAction>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(tenant =>
        {
            tenant.HasIndex(t => t.Slug).IsUnique();
            tenant.Property(t => t.Slug).HasMaxLength(40);
            tenant.Property(t => t.DisplayName).HasMaxLength(100);
            tenant.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            tenant.Property(t => t.Plan).HasConversion<string>().HasMaxLength(20);
            tenant.OwnsOne(t => t.Branding, branding => branding.ToJson());
        });

        modelBuilder.Entity<Membership>(membership =>
        {
            // Leads with tenant_id, like every tenant-owned index (design §4).
            membership.HasIndex(m => new { m.TenantId, m.UserId }).IsUnique();
            membership.HasIndex(m => m.UserId);
            membership.Property(m => m.Role).HasConversion<string>().HasMaxLength(20);
            membership.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserProfile>(profile =>
        {
            profile.HasIndex(p => p.UserId).IsUnique();
            profile.Property(p => p.Email).HasMaxLength(320);
            profile.Property(p => p.DisplayName).HasMaxLength(100);
        });

        modelBuilder.Entity<StaffInvite>(invite =>
        {
            invite.HasIndex(i => new { i.TenantId, i.TokenHash }).IsUnique();
            // One open invite per address and workspace; inviting again replaces it.
            invite.HasIndex(i => new { i.TenantId, i.Email }).IsUnique().HasFilter("accepted_at IS NULL");
            invite.Property(i => i.Email).HasMaxLength(320);
            invite.Property(i => i.TokenHash).HasMaxLength(64).IsFixedLength();
            invite.HasOne<Tenant>().WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ContentReport>(report =>
        {
            report.HasIndex(r => new { r.ResolvedAt, r.ReportedAt });
            report.HasIndex(r => r.ProgramId);
            report.HasIndex(r => r.TenantId);
            report.Property(r => r.Kind).HasConversion<string>().HasMaxLength(20);
            report.Property(r => r.Reason).HasConversion<string>().HasMaxLength(20);
            report.Property(r => r.Outcome).HasConversion<string>().HasMaxLength(20);
            report.Property(r => r.Subject).HasMaxLength(120);
            report.Property(r => r.CoachSlug).HasMaxLength(40);
            report.Property(r => r.Note).HasMaxLength(500);
        });

        modelBuilder.ConfigureOutbox();
        modelBuilder.ConfigureAdminActions();

        base.OnModelCreating(modelBuilder);
    }
}
