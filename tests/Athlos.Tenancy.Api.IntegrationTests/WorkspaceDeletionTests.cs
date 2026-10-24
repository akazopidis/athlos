using System.Net;
using System.Net.Http.Json;
using Athlos.Tenancy;
using Athlos.Testing;
using Npgsql;
using static Athlos.Testing.TenancyArrange;

namespace Athlos.Tenancy.Api.IntegrationTests;

// Deleting a workspace (design §3.4): the owner confirms with the address, the other services erase
// their part first, then the team and invites go and the workspace is marked Deleted, its address
// kept so no one inherits the old links.
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

    [Fact]
    public async Task If_another_service_cannot_erase_the_workspace_stays_and_can_be_deleted_again()
    {
        var workspace = await _api.CreateWorkspace();
        _api.Erasure.FailFor(workspace.TenantId);

        var failed = await Delete(workspace, confirm: workspace.Slug);

        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Equal("Active", await Scalar<string>("SELECT status FROM tenants WHERE id = @tenant", workspace.TenantId));
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM memberships WHERE tenant_id = @tenant", workspace.TenantId));
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
