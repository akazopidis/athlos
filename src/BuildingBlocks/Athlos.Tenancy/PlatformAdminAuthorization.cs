using Microsoft.Extensions.DependencyInjection;

namespace Athlos.Tenancy;

// The platform admin (design §1, §5): one Entra app role on the API app registration, assigned to a
// user in the External ID tenant, arrives in the access token's "roles" claim. Coaches and customers
// are told apart by memberships in tenancy-api instead (design §5). Any service with admin routes
// (tenancy: workspaces; ordering: coach payouts) uses this one policy.
public static class PlatformAdminAuthorization
{
    public const string Role = "Platform.Admin";
    public const string Policy = "PlatformAdmin";

    public static IServiceCollection AddPlatformAdmin(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(Policy, policy => policy.RequireAuthenticatedUser().RequireClaim("roles", Role));
        return services;
    }
}
