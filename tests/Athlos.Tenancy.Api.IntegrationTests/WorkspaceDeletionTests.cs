using System.Net;
using System.Net.Http.Json;
using Athlos.Tenancy;
using Athlos.Tenancy.Api.Features.Workspace;
using Athlos.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Athlos.Testing.TenancyArrange;

namespace Athlos.Tenancy.Api.IntegrationTests;

// Deleting a workspace (design §3.4): the owner (or the platform admin) starts it, the other services
// erase their part, then the team and invites go and the workspace is marked Deleted, its address
// kept so no one inherits the old links. A deletion that cannot finish at once is retried.
public sealed class WorkspaceDeletionTests(AthlosDatabases databases) : IAsyncLifetime
{
    private readonly TenancyApiFactory _api = new(databases);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_owner_deletes_the_workspace_and_its_team_with_it()
    {
        var workspace = await _api.CreateWorkspace();
        await databases.AddMember(workspace.TenantId, TenantRole.Staff);

        var deleted = await Delete(workspace, confirm: workspace.Slug);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Contains(workspace.TenantId, _api.Erasure.Erased);
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM memberships WHERE tenant_id = @tenant", workspace.TenantId));
        Assert.Equal("Deleted", await Scalar<string>("SELECT status FROM tenants WHERE id = @tenant", workspace.TenantId));
        Assert.Equal("Deleted workspace", await Scalar<string>("SELECT display_name FROM tenants WHERE id = @tenant", workspace.TenantId));

        var storefront = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/coaches/{workspace.Slug}"));
        var membership = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/tenants/{workspace.Slug}/members/{workspace.OwnerId}"));
        var reused = await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenants") { Content = JsonContent.Create(new { slug = workspace.Slug, displayName = "Copycat", termsVersion = TenancyArrange.CoachTermsVersion }) },
            userId: Guid.CreateVersion7());
        Assert.Equal(HttpStatusCode.NotFound, storefront.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, membership.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);

        // The gateway caches memberships for a minute, so studio requests may still arrive with the
        // old tenant headers; the workspace is gone for them too.
        Assert.Equal(HttpStatusCode.NotFound, (await _api.AsOwner(workspace, HttpMethod.Get, "/workspace")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.AsOwner(workspace, HttpMethod.Get, "/invites")).StatusCode);
    }

    [Fact]
    public async Task Staff_cannot_delete_it_and_a_wrong_confirmation_deletes_nothing()
    {
        var workspace = await _api.CreateWorkspace();
        var staff = await databases.AddMember(workspace.TenantId, TenantRole.Staff);

        var asStaff = await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/studio/{workspace.Slug}/workspace") { Content = JsonContent.Create(new { confirmSlug = workspace.Slug }) },
            staff, workspace.TenantId, TenantRole.Staff);
        var mistyped = await Delete(workspace, confirm: "not-the-address");

        Assert.Equal(HttpStatusCode.Forbidden, asStaff.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, mistyped.StatusCode);
        Assert.DoesNotContain(workspace.TenantId, _api.Erasure.Erased);
        Assert.Equal("Active", await Scalar<string>("SELECT status FROM tenants WHERE id = @tenant", workspace.TenantId));
    }

    // One durable job (backlog item 54): if another service cannot erase its part, the deletion has still
    // started (202): the programs are hidden, the studio closed, the owner sees it pending, and the
    // retries finish it once the service is back. An attempt holds a lease: it is not repeated at once.
    [Fact]
    public async Task A_deletion_that_cannot_finish_at_once_is_finished_by_the_retries()
    {
        var workspace = await _api.CreateWorkspace();
        _api.Erasure.FailFor(workspace.TenantId);

        var started = await Delete(workspace, confirm: workspace.Slug);
        var memberships = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, "/api/v1/me/memberships"), workspace.OwnerId);
        var leased = await WorkspaceDeletions.RunAsync(Scopes(), workspace.TenantId, Cancel);

        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        Assert.True(_api.Suspension.Suspended[workspace.TenantId]);
        Assert.Equal("Suspended", await Scalar<string>("SELECT status FROM tenants WHERE id = @tenant", workspace.TenantId));
        Assert.Contains("\"deletionPending\":true", await memberships.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
        Assert.False(leased);
        Assert.Equal(1, await Scalar<int>("SELECT attempts FROM workspace_deletions WHERE tenant_id = @tenant", workspace.TenantId));
        Assert.StartsWith("The other services could not erase",
            await Scalar<string>("SELECT last_error FROM workspace_deletions WHERE tenant_id = @tenant", workspace.TenantId), StringComparison.Ordinal);

        _api.Erasure.Recover(workspace.TenantId);
        await Execute("UPDATE workspace_deletions SET next_attempt_at = now() - interval '1 second' WHERE tenant_id = @tenant", workspace.TenantId);
        await WorkspaceDeletions.RunDueAsync(Scopes(), Cancel);

        Assert.Contains(workspace.TenantId, _api.Erasure.Erased);
        Assert.Equal("Deleted", await Scalar<string>("SELECT status FROM tenants WHERE id = @tenant", workspace.TenantId));
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM workspace_deletions WHERE tenant_id = @tenant", workspace.TenantId));
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM memberships WHERE tenant_id = @tenant", workspace.TenantId));
    }

    // What can refuse refuses before anything changes: sales still refundable (409), or catalog-api
    // not answering when the programs must be hidden (502).
    [Fact]
    public async Task A_refused_or_unreachable_start_changes_nothing()
    {
        var (refundable, unreachable) = (await _api.CreateWorkspace(), await _api.CreateWorkspace());
        _api.Erasure.RefuseFor(refundable.TenantId, "Sales can still be refunded until 2 November.");
        _api.Suspension.FailFor(unreachable.TenantId);

        var refused = await Delete(refundable, confirm: refundable.Slug);
        var failed = await Delete(unreachable, confirm: unreachable.Slug);

        Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.BadGateway), (refused.StatusCode, failed.StatusCode));
        foreach (var workspace in new[] { refundable, unreachable })
        {
            Assert.Equal("Active", await Scalar<string>("SELECT status FROM tenants WHERE id = @tenant", workspace.TenantId));
            Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM workspace_deletions WHERE tenant_id = @tenant", workspace.TenantId));
        }
    }

    // The platform admin deletes a workspace with a reason in the audit trail; while a deletion is
    // pending the workspace cannot be reactivated.
    [Fact]
    public async Task The_platform_admin_deletes_a_workspace_with_a_reason()
    {
        var (deleted, pending) = (await _api.CreateWorkspace(), await _api.CreateWorkspace());
        _api.Erasure.FailFor(pending.TenantId);
        var admin = Guid.CreateVersion7();

        var noReason = await AdminDelete(admin, deleted.TenantId, reason: null);
        var done = await AdminDelete(admin, deleted.TenantId, "Sells pirated programs");
        var started = await AdminDelete(admin, pending.TenantId, "The owner asked by email");
        var reactivate = await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Put, $"/api/v1/admin/tenants/{pending.TenantId}/status")
            {
                Content = JsonContent.Create(new { status = "Active", reason = "Changed my mind" }),
            },
            admin, appRoles: AdminRole);
        var list = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/admin/tenants?q={pending.Slug}"), admin, appRoles: AdminRole);
        var asCoach = await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/tenants/{pending.TenantId}/deletion") { Content = JsonContent.Create(new { reason = "Not an admin" }) },
            pending.OwnerId);

        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.Accepted), (done.StatusCode, started.StatusCode));
        Assert.Equal(HttpStatusCode.Conflict, reactivate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asCoach.StatusCode);
        Assert.Equal("Deleted", await Scalar<string>("SELECT status FROM tenants WHERE id = @tenant", deleted.TenantId));
        Assert.Equal("Sells pirated programs",
            await Scalar<string>("SELECT reason FROM admin_actions WHERE tenant_id = @tenant AND action = 'WorkspaceDeleted'", deleted.TenantId));
        Assert.Contains("\"deletionPending\":true", await list.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
    }

    private static readonly string[] AdminRole = ["Platform.Admin"];

    private Task<HttpResponseMessage> AdminDelete(Guid admin, Guid tenantId, string? reason) =>
        _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/tenants/{tenantId}/deletion") { Content = JsonContent.Create(new { reason }) },
            admin, appRoles: AdminRole);

    private IServiceScopeFactory Scopes() => _api.Services.GetRequiredService<IServiceScopeFactory>();

    private async Task Execute(string sql, Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(databases.OwnerConnectionString("tenancy"));
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        await command.ExecuteNonQueryAsync(Cancel);
    }

    private Task<HttpResponseMessage> Delete(Workspace workspace, string confirm) =>
        _api.AsOwner(workspace, HttpMethod.Delete, "/workspace", new { confirmSlug = confirm });

    // As the superuser, so RLS cannot hide what is left.
    private async Task<T> Scalar<T>(string sql, Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(databases.OwnerConnectionString("tenancy"));
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        return (T)(await command.ExecuteScalarAsync(Cancel))!;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();
}
