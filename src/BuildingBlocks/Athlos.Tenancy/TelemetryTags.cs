namespace Athlos.Tenancy;

// Names of the tenant-aware telemetry attributes (design §6), the same in every service and in the
// gateway, so one App Insights query can filter all of them by coach.
public static class TelemetryTags
{
    public const string TenantId = "tenant.id";
    public const string TenantPlan = "tenant.plan";
    public const string UserRole = "user.role";
}
