using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Data;

public sealed class TenancyDbContext(DbContextOptions<TenancyDbContext> options, ITenantContext tenant)
    : TenantDbContext(options, tenant)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();

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

        base.OnModelCreating(modelBuilder);
    }
}
