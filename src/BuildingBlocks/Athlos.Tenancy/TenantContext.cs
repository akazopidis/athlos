namespace Athlos.Tenancy;

// Who is calling and on behalf of which tenant (design §3.2, layer 2). Populated per request by
// UseTenantContext: the tenant and role from the gateway's signed headers, the user from the JWT.
// Endpoints read the tenant from here only, never from a request body or query string.
public interface ITenantContext
{
    Guid? TenantId { get; }

    Guid? UserId { get; }

    TenantRole? Role { get; }

    // Set only by slices listed as cross-tenant in design §3.2 (public marketplace reads).
    TenantScope Scope { get; }
}

public enum TenantRole
{
    Owner,
    Staff,
}

public enum TenantScope
{
    // Only the current tenant's rows (and, where a policy allows it, the caller's own rows).
    Tenant,

    // Also published rows of every tenant (RLS: app.scope = 'public-read').
    PublicRead,
}

public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }

    public Guid? UserId { get; private set; }

    public TenantRole? Role { get; private set; }

    public TenantScope Scope { get; private set; } = TenantScope.Tenant;

    internal void Populate(Guid? tenantId, Guid? userId, TenantRole? role)
    {
        TenantId = tenantId;
        UserId = userId;
        Role = role;
    }

    // For slices that decide the tenant themselves instead of the gateway: creating a tenant, and
    // the internal membership lookup the gateway calls. Call before the transaction starts, so the
    // database session sees the same tenant.
    public void EnterTenant(Guid tenantId, TenantRole? role)
    {
        if (TenantId is not null && TenantId != tenantId)
        {
            throw new InvalidOperationException("Already acting for another tenant.");
        }

        TenantId = tenantId;
        Role = role;
        System.Diagnostics.Activity.Current?.SetTag(TelemetryTags.TenantId, tenantId.ToString());
    }

    // For internal slices that act for a user another service already verified (account deletion,
    // design §5.2): RLS then shows that user's own rows across tenants, as it does for the user.
    public void ActForUser(Guid userId)
    {
        if (UserId is not null && UserId != userId)
        {
            throw new InvalidOperationException("Already acting for another user.");
        }

        UserId = userId;
    }

    // For public marketplace slices only (design §3.2).
    public void UsePublicReadScope() => Scope = TenantScope.PublicRead;
}
