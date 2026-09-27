using System.Net;
using System.Net.Http.Json;
using Athlos.Tenancy;
using Athlos.Testing;

namespace Athlos.IsolationTests;

// Design §3.3: for every tenant-scoped endpoint, call with tenant B's identity against tenant A's
// resources and expect 404 (not 403, which would reveal that A's resource exists).
public sealed class CrossTenantEndpointTests(TenancyDatabase database) : IAsyncLifetime
{
    private readonly TenancyApiFactory _api = new(database);
    private TenancyArrange.Workspace _a = null!;
    private TenancyArrange.Workspace _b = null!;

    public async ValueTask InitializeAsync()
    {
        _a = await _api.CreateWorkspace();
        _b = await _api.CreateWorkspace();
    }

    [Fact]
    public async Task B_cannot_read_As_workspace()
    {
        var response = await AsOwnerOfB(HttpMethod.Get, $"/api/v1/studio/{_a.Slug}/workspace");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task B_cannot_update_As_workspace()
    {
        var response = await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Put, $"/api/v1/studio/{_a.Slug}/workspace")
            {
                Content = JsonContent.Create(new { displayName = "Hijacked" }),
            },
            _b.OwnerId, _b.TenantId, TenantRole.Owner);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var storefront = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/coaches/{_a.Slug}"));
        Assert.DoesNotContain("Hijacked", await storefront.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_studio_request_without_a_resolved_tenant_is_not_found()
    {
        // As if the gateway had not resolved a membership: signed, but no tenant or role.
        var response = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/studio/{_a.Slug}/workspace"), _a.OwnerId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Bs_memberships_do_not_include_A()
    {
        var response = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, "/api/v1/me/memberships"), _b.OwnerId);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(_a.TenantId.ToString(), body, StringComparison.Ordinal);
        Assert.Contains(_b.TenantId.ToString(), body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_owner_of_B_is_not_a_member_of_A()
    {
        var response = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/tenants/{_a.Slug}/members/{_b.OwnerId}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private Task<HttpResponseMessage> AsOwnerOfB(HttpMethod method, string path) =>
        _api.SendAsGateway(new HttpRequestMessage(method, path), _b.OwnerId, _b.TenantId, TenantRole.Owner);

    public ValueTask DisposeAsync() => _api.DisposeAsync();
}
