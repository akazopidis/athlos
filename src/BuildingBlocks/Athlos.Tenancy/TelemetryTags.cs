namespace Athlos.Tenancy;

// Names of the tenant-aware telemetry attributes (design §6), the same in every service and in the
// gateway, so one App Insights query can filter all of them by coach.
public static class TelemetryTags
{
    public const string TenantId = "tenant.id";

    // The coach's public storefront address: readable in charts, and not personal data.
    public const string TenantSlug = "tenant.slug";
    public const string TenantPlan = "tenant.plan";
    public const string UserRole = "user.role";
}
