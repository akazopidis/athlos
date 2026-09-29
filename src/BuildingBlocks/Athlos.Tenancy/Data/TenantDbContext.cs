using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Data;

// Base DbContext for every service (design §3.2, layer 3):
// - a named query filter on every ITenantOwned entity, so queries only see the current tenant;
// - SaveChanges stamps TenantId on inserts and refuses to change another tenant's rows.
// Layer 4 (RLS) enforces the same rule in PostgreSQL, even if this layer has a bug.
public abstract class TenantDbContext(DbContextOptions options, ITenantContext tenant) : DbContext(options)
{
    public const string TenantFilter = "tenant";

    // Read by the query filter on every query, so the filter follows the current request.
    protected Guid? CurrentTenantId => tenant.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType) && entityType.BaseType is null)
            {
                modelBuilder.Entity(entityType.ClrType).HasQueryFilter(TenantFilter, TenantFilterFor(entityType.ClrType));
            }
        }
    }

    // e => e.TenantId == this.CurrentTenantId
    private LambdaExpression TenantFilterFor(Type clrType)
    {
        var entity = Expression.Parameter(clrType, "e");
        var tenantId = Expression.Convert(Expression.Property(entity, nameof(ITenantOwned.TenantId)), typeof(Guid?));
        var current = Expression.Property(Expression.Constant(this), nameof(CurrentTenantId));
        return Expression.Lambda(Expression.Equal(tenantId, current), entity);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BeforeSave();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BeforeSave();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void BeforeSave()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries<IHasTimestamps>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<ITenantOwned>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.TenantId = CurrentTenantId
                        ?? throw new InvalidOperationException($"Cannot add {entry.Metadata.ClrType.Name} without a tenant.");
                    break;

                case EntityState.Modified or EntityState.Deleted
                    when (Guid?)entry.Property(nameof(ITenantOwned.TenantId)).OriginalValue != CurrentTenantId
                        || entry.Entity.TenantId != CurrentTenantId:
                    throw new InvalidOperationException(
                        $"Refusing to {entry.State.ToString().ToLowerInvariant()} a {entry.Metadata.ClrType.Name} of another tenant.");
            }
        }
    }
}
