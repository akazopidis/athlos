using System.Net;
using System.Net.Http.Headers;
using Athlos.Tenancy.Api.Data;
using Athlos.Tenancy.Data;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Athlos.Tenancy.Api.Features.Account;

// Deleting your account (design §5.2): Athlos erases or anonymizes your data in every service, then
// deletes your Entra user through Microsoft Graph, so you can no longer sign in.
//
// 1. Not while you own a workspace: delete it first (§3.4) or hand it over, so nothing that coaches'
//    customers paid for disappears as a side effect (409, listing them).
// 2. ordering-api anonymizes your orders (kept for the coaches' accounting); enrollment-api erases
//    your enrollments and progress.
// 3. Here: your staff memberships and the invites sent to you, one workspace at a time, then your
//    profile.
// 4. Last, the Entra user. If a step fails, the answer says so and a retry finishes the job: every
//    step is idempotent.
//
// CROSS-TENANT SLICE (design §3.2, "a user's own memberships"): finding your workspaces ignores the
// tenant filter and relies on the user filter below and on RLS (user_id = app.user_id), as
// ListMyMemberships does.
public static class DeleteMyAccount
{
    public sealed record OwnedWorkspaces(List<string> Workspaces);

    public static void MapDeleteMyAccount(this IEndpointRouteBuilder app) =>
        app.MapDelete("/api/v1/me", Handle)
            .RequireAuthorization()
            .WithName("DeleteMyAccount")
            .WithTags("Account");

    private static async Task<Results<NoContent, ProblemHttpResult>> Handle(
        TenantContext tenant, TenancyDbContext db, ICustomerErasure erasure, IEntraUsers entra, IServiceScopeFactory scopes,
        CancellationToken cancellationToken)
    {
        if (tenant.UserId is not { } userId)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!entra.Configured)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Account deletion is not available in this environment.");
        }

        List<string> owned;
        List<Guid> staffIn;
        string? email;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var memberships = await (
                    from m in db.Memberships.IgnoreQueryFilters([TenantDbContext.TenantFilter])
                    where m.UserId == userId
                    from t in db.Tenants
                    where t.Id == m.TenantId && t.Status != TenantStatus.Deleted
                    select new { m.TenantId, t.Slug, m.Role })
                .ToListAsync(cancellationToken);
            owned = memberships.Where(m => m.Role == TenantRole.Owner).Select(m => m.Slug).Order().ToList();
            staffIn = memberships.Where(m => m.Role == TenantRole.Staff).Select(m => m.TenantId).ToList();
            email = await db.UserProfiles.Where(p => p.UserId == userId).Select(p => p.Email).SingleOrDefaultAsync(cancellationToken);
        }

        if (owned.Count > 0)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: $"Delete the workspaces you own first: {string.Join(", ", owned)}.",
                extensions: new Dictionary<string, object?> { ["workspaces"] = owned });
        }

        try
        {
            await erasure.EraseCustomerAsync(userId, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Your account could not be deleted yet. Nothing is lost by trying again.");
        }

        foreach (var tenantId in staffIn)
        {
            await scopes.InTenantAsync<TenancyDbContext>(tenantId, async workspace =>
            {
                await workspace.Memberships.Where(m => m.UserId == userId).ExecuteDeleteAsync(cancellationToken);
                if (email is not null)
                {
                    await workspace.StaffInvites.Where(i => i.Email == email).ExecuteDeleteAsync(cancellationToken);
                }
            }, cancellationToken);
        }

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            // RLS own_profile: a session reaches only the profile of app.user_id.
            await db.UserProfiles.Where(p => p.UserId == userId).ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        try
        {
            await entra.DeleteUserAsync(userId, cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or AuthenticationFailedException)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Your data in Athlos is deleted, but your sign-in account could not be removed yet. Try again.");
        }

        return TypedResults.NoContent();
    }
}

// The other services' part of an account deletion, through their signed internal endpoints.
public interface ICustomerErasure
{
    Task EraseCustomerAsync(Guid customerId, CancellationToken cancellationToken);
}

public sealed class ServiceCustomerErasure(IHttpClientFactory clients) : ICustomerErasure
{
    public const string Ordering = "erase-customer-ordering";
    public const string Enrollment = "erase-customer-enrollment";

    public async Task EraseCustomerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        foreach (var service in (string[])[Ordering, Enrollment])
        {
            using var response = await clients.CreateClient(service).DeleteAsync($"/internal/customers/{customerId}", cancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }
}

// The user's Entra External ID account.
public interface IEntraUsers
{
    // False where no Graph app is set up (locally, or a stage before infra/account_deletion_setup.py).
    bool Configured { get; }

    Task DeleteUserAsync(Guid userId, CancellationToken cancellationToken);
}

// Configuration (EntraAccountDeletion section): TenantId (the External ID tenant), ClientId (its
// "Athlos account deletion" app, Graph User.ReadWrite.All) and ClientSecret (from Key Vault).
public sealed class EntraAccountDeletionOptions
{
    public string? TenantId { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }
}

public sealed class GraphEntraUsers(HttpClient http, IOptions<EntraAccountDeletionOptions> options) : IEntraUsers
{
    private static readonly string[] GraphScope = ["https://graph.microsoft.com/.default"];

    private readonly EntraAccountDeletionOptions _options = options.Value;

    public bool Configured =>
        !string.IsNullOrWhiteSpace(_options.TenantId) && !string.IsNullOrWhiteSpace(_options.ClientId) && !string.IsNullOrWhiteSpace(_options.ClientSecret);

    public async Task DeleteUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var credential = new ClientSecretCredential(_options.TenantId, _options.ClientId, _options.ClientSecret);
        var token = await credential.GetTokenAsync(new TokenRequestContext(GraphScope), cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"https://graph.microsoft.com/v1.0/users/{userId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var response = await http.SendAsync(request, cancellationToken);

        // Already gone (a retry after a timeout) counts as done.
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }
}
