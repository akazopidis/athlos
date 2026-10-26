using Athlos.Tenancy.Api.Data;
using Athlos.Tenancy.Data;

namespace Athlos.Tenancy.Api.Features.Admin;

// The platform admin's workspace routes (design §1, §3.4), behind the Platform.Admin app role
// (PlatformAdminAuthorization, Athlos.Tenancy). /actions is their audit trail (admin_actions).
public static class PlatformAdmin
{
    // Admin routes act on every workspace, so they have no tenant of their own: a slice that changes
    // a workspace enters that one tenant for its transaction, and RLS confines the write to it.
    public static RouteGroupBuilder MapPlatformAdmin(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/admin")
            .RequireAuthorization(PlatformAdminAuthorization.Policy)
            .WithTags("Admin");
        admin.MapListTenants();
        admin.MapSetTenantStatus();
        admin.MapSetTenantPlan();
        admin.MapGetPlatformMetrics();
        admin.MapAdminActions<TenancyDbContext>("/actions").WithName("AdminListWorkspaceActions");
        return admin;
    }
}
