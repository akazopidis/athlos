namespace Athlos.Tenancy;

// Headers only the gateway may set (design §3.2). The gateway strips any incoming copies,
// sets its own, and signs them (GatewaySignature).
public static class TrustedHeaders
{
    public const string TenantId = "X-Tenant-Id";
    public const string UserRole = "X-User-Role";

    public static IReadOnlyList<string> All { get; } =
        [TenantId, UserRole, GatewaySignature.TimestampHeader, GatewaySignature.SignatureHeader];
}
