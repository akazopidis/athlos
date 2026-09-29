namespace Athlos.Tenancy.Api.Features.Tenants;

// Workspace addresses: /c/{slug} and /studio/{slug} (design §8).
public static class Slugs
{
    // Lowercase letters and digits, single dashes between them.
    public const string Pattern = "^[a-z0-9]+(-[a-z0-9]+)*$";

    private static readonly HashSet<string> Reserved =
    [
        "admin", "api", "app", "athlos", "c", "help", "internal", "marketplace", "me", "new",
        "settings", "studio", "support", "webhooks", "www",
    ];

    public static bool IsReserved(string slug) => Reserved.Contains(slug);
}
