namespace Athlos.Tenancy.Api.Features.Admin;

// The platform admin (design §1, §5): one Entra app role on the API app registration, assigned to a
// user in the External ID tenant, arrives in the access token's "roles" claim. Coaches and customers
// are told apart by memberships in this database instead (design §5).
public static class PlatformAdmin
{
    public const string Role = "Platform.Admin";
    public const string Policy = "PlatformAdmin";

    public static IServiceCollection AddPlatformAdmin(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(Policy, policy => policy.RequireAuthenticatedUser().RequireClaim("roles", Role));
        return services;
    }

    // Admin routes act on every workspace, so they have no tenant of their own: a slice that changes
    // a workspace enters that one tenant for its transaction, and RLS confines the write to it.
    public static RouteGroupBuilder MapPlatformAdmin(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/admin")
            .RequireAuthorization(Policy)
            .WithTags("Admin");
        admin.MapListTenants();
        admin.MapSetTenantStatus();
        admin.MapSetTenantPlan();
        admin.MapGetPlatformMetrics();
        return admin;
    }
}
