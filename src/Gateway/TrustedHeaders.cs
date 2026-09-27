namespace Athlos.Gateway;

public static class TrustedHeaders
{
    public const string TenantId = "X-Tenant-Id";
    public const string UserRole = "X-User-Role";

    public static IReadOnlyList<string> All { get; } = [TenantId, UserRole];
}
