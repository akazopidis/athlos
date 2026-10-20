using System.Net.Http.Json;
using System.Text.Json;
using Athlos.Tenancy.Api.Data;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Athlos.Tenancy.Api.Features.Account;

// "Download my data" (design §5.4, the GDPR's right of access): everything Athlos holds about the
// signed-in user, in one JSON document. Here: the profile and the workspaces they coach in. The other
// services' parts come from their signed internal export endpoints, which read the user's own rows
// only: orders and checkouts (ordering), programs, progress and reviews (enrollment), saved programs
// (catalog). If one cannot answer, nothing partial is handed out (502, try again).
//
// CROSS-TENANT SLICE (design §3.2, "a user's own memberships"): the workspaces ignore the tenant filter
// and rely on the user filter below and on RLS (user_id = app.user_id), as ListMyMemberships does.
public static class ExportMyData
{
    public sealed record Profile(Guid UserId, string? Email, string? DisplayName);

    public sealed record Workspace(string Slug, string DisplayName, TenantRole Role, DateTimeOffset Since);

    public sealed record Response(
        DateTimeOffset ExportedAt, Profile Account, List<Workspace> Workspaces, JsonElement Orders, JsonElement Programs, JsonElement Saved);

    public static void MapExportMyData(this IEndpointRouteBuilder me) =>
        me.MapGet("/export", Handle)
            .WithName("ExportMyData")
            .WithTags("Account");

    private static async Task<Results<Ok<Response>, ProblemHttpResult>> Handle(
        TenantContext tenant, TenancyDbContext db, ICustomerExport export, TimeProvider time, HttpContext http, CancellationToken cancellationToken)
    {
        if (tenant.UserId is not { } userId)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized);
        }

        Profile profile;
        List<Workspace> workspaces;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var stored = await db.UserProfiles.AsNoTracking().Where(p => p.UserId == userId).SingleOrDefaultAsync(cancellationToken);
            profile = new Profile(userId, stored?.Email, stored?.DisplayName);
            workspaces = await (
                    from m in db.Memberships.AsNoTracking().IgnoreQueryFilters([TenantDbContext.TenantFilter])
                    where m.UserId == userId
                    from t in db.Tenants
                    where t.Id == m.TenantId && t.Status != TenantStatus.Deleted
                    orderby m.CreatedAt
                    select new Workspace(t.Slug, t.DisplayName, m.Role, m.CreatedAt))
                .ToListAsync(cancellationToken);
        }

        CustomerData data;
        try
        {
            data = await export.GetAsync(userId, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Your data could not be gathered just now. Try again in a moment.");
        }

        // Saved as a file by the browser, never cached on the way.
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"athlos-data-{time.GetUtcNow():yyyy-MM-dd}.json\"";
        return TypedResults.Ok(new Response(time.GetUtcNow(), profile, workspaces, data.Orders, data.Programs, data.Saved));
    }
}

public sealed record CustomerData(JsonElement Orders, JsonElement Programs, JsonElement Saved);

// The other services' parts of an export, through their signed internal endpoints.
public interface ICustomerExport
{
    Task<CustomerData> GetAsync(Guid customerId, CancellationToken cancellationToken);
}

public sealed class ServiceCustomerExport(IHttpClientFactory clients) : ICustomerExport
{
    public async Task<CustomerData> GetAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var orders = await GetAsync(ServiceCustomerErasure.Ordering, customerId, cancellationToken);
        var programs = await GetAsync(ServiceCustomerErasure.Enrollment, customerId, cancellationToken);
        var saved = await GetAsync(ServiceCustomerErasure.Catalog, customerId, cancellationToken);
        return new CustomerData(orders, programs, saved);
    }

    private async Task<JsonElement> GetAsync(string service, Guid customerId, CancellationToken cancellationToken)
    {
        using var response = await clients.CreateClient(service).GetAsync($"/internal/customers/{customerId}/export", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }
}
