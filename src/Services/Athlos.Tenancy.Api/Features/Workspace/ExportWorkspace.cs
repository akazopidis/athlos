using System.Net.Http.Json;
using System.Text.Json;
using Athlos.Tenancy.Api.Data;
using Athlos.Tenancy.Api.Features.Plans;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Workspace;

// GET /api/v1/studio/{slug}/workspace/export (design §5.4): the owner's export of their workspace, one
// JSON file. tenancy-api's part (the workspace, its storefront settings, the accepted Terms for coaches
// and the team) and each service's part from a signed GET /internal/tenants/{id}/export that enters
// this tenant and reads its rows only: catalog's programs and workouts, ordering's sales and discount
// codes, enrollment's customers per program and reviews. If a service cannot answer, nothing partial
// is handed out (502). Owners only: the file holds the paid content and customers' addresses.
public static class ExportWorkspace
{
    public sealed record Workspace(
        string Slug, string DisplayName, TenantStatus Status, PlanName Plan, Branding Branding, DateTimeOffset CreatedAt, string? CoachTermsVersion,
        DateTimeOffset? CoachTermsAcceptedAt);

    public sealed record Member(string? DisplayName, string? Email, TenantRole Role, DateTimeOffset Since);

    public sealed record Response(
        DateTimeOffset ExportedAt, Workspace Workspace, List<Member> Team, JsonElement Programs, JsonElement Sales, JsonElement Customers);

    public static void MapExportWorkspace(this IEndpointRouteBuilder app) =>
        app.MapGet("/workspace/export", Handle)
            .WithName("ExportWorkspace")
            .WithTags("Workspace");

    private static async Task<Results<Ok<Response>, NotFound, ProblemHttpResult>> Handle(
        string tenantSlug, ITenantContext tenant, TenancyDbContext db, ITenantExport export, TimeProvider time, HttpContext http,
        CancellationToken cancellationToken)
    {
        var workspace = await db.Tenants.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == tenant.TenantId && t.Slug == tenantSlug && t.Status != TenantStatus.Deleted, cancellationToken);
        if (workspace is null)
        {
            return TypedResults.NotFound();
        }

        // Inside their own tenant a member may learn the workspace exists, so 403 is fine here.
        if (tenant.Role != TenantRole.Owner)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only the owner can download the workspace's data.");
        }

        // Members' profiles are readable inside the tenant (user_profiles tenant_members_read).
        var team = await db.Memberships.AsNoTracking()
            .GroupJoin(db.UserProfiles, m => m.UserId, p => p.UserId, (m, profiles) => new { m, profiles })
            .SelectMany(x => x.profiles.DefaultIfEmpty(), (x, p) => new { x.m.Role, x.m.CreatedAt, p!.DisplayName, p.Email })
            .OrderBy(x => x.Role)
            .ThenBy(x => x.CreatedAt)
            .Select(x => new Member(x.DisplayName, x.Email, x.Role, x.CreatedAt))
            .ToListAsync(cancellationToken);

        TenantData data;
        try
        {
            data = await export.GetAsync(workspace.Id, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The workspace's data could not be gathered just now. Try again in a moment.");
        }

        // Saved as a file by the browser, never cached on the way.
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"athlos-{workspace.Slug}-{time.GetUtcNow():yyyy-MM-dd}.json\"";
        return TypedResults.Ok(new Response(
            time.GetUtcNow(),
            new Workspace(workspace.Slug, workspace.DisplayName, workspace.Status, workspace.Plan, workspace.Branding, workspace.CreatedAt,
                workspace.CoachTermsVersion, workspace.CoachTermsAcceptedAt),
            team, data.Programs, data.Sales, data.Customers));
    }
}

public sealed record TenantData(JsonElement Programs, JsonElement Sales, JsonElement Customers);

// The other services' parts of a workspace export, through their signed internal endpoints.
public interface ITenantExport
{
    Task<TenantData> GetAsync(Guid tenantId, CancellationToken cancellationToken);
}

public sealed class ServiceTenantExport(IHttpClientFactory clients) : ITenantExport
{
    public async Task<TenantData> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
        new(await GetAsync(ServiceTenantErasure.Catalog, tenantId, cancellationToken),
            await GetAsync(ServiceTenantErasure.Ordering, tenantId, cancellationToken),
            await GetAsync(ServiceTenantErasure.Enrollment, tenantId, cancellationToken));

    private async Task<JsonElement> GetAsync(string service, Guid tenantId, CancellationToken cancellationToken)
    {
        using var response = await clients.CreateClient(service).GetAsync($"/internal/tenants/{tenantId}/export", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }
}
