using System.Net;
using System.Net.Http.Json;
using Athlos.Tenancy;
using Athlos.Testing;

namespace Athlos.IsolationTests;

// Design §3.3: for every tenant-scoped endpoint, call with tenant B's identity against tenant A's
// resources and expect 404 (not 403, which would reveal that A's resource exists).
public sealed class CrossTenantEndpointTests(AthlosDatabases database) : IAsyncLifetime
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

    // The Terms for coaches (design §5.4): B's owner cannot accept them on A's behalf.
    [Fact]
    public async Task B_cannot_accept_the_coach_terms_for_A()
    {
        var response = await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Post, $"/api/v1/studio/{_a.Slug}/workspace/coach-terms")
            {
                Content = JsonContent.Create(new { termsVersion = TenancyArrange.CoachTermsVersion }),
            },
            _b.OwnerId, _b.TenantId, TenantRole.Owner);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // The workspace export (design §5.4): B's owner cannot download A's.
    [Fact]
    public async Task B_cannot_download_As_workspace_data()
    {
        var response = await AsOwnerOfB(HttpMethod.Get, $"/api/v1/studio/{_a.Slug}/workspace/export");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // The coach's logo (design §8): B's owner can neither replace nor remove A's.
    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task B_cannot_change_As_logo(string method)
    {
        var response = await _api.SendAsGateway(
            new HttpRequestMessage(new HttpMethod(method), $"/api/v1/studio/{_a.Slug}/workspace/logo") { Content = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) },
            _b.OwnerId, _b.TenantId, TenantRole.Owner);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/payouts/onboarding")]
    [InlineData("GET", "/payouts")]
    public async Task B_cannot_reach_As_payouts(string method, string path)
    {
        var response = await AsOwnerOfB(new HttpMethod(method), $"/api/v1/studio/{_a.Slug}{path}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // The team (design §5.2): B's owner cannot see A's team, remove A's owner or take A over.
    [Theory]
    [InlineData("GET", "/members")]
    [InlineData("DELETE", "/members/{owner}")]
    [InlineData("POST", "/members/{owner}/ownership")]
    public async Task B_cannot_reach_As_team(string method, string path)
    {
        var response = await AsOwnerOfB(new HttpMethod(method), $"/api/v1/studio/{_a.Slug}{path.Replace("{owner}", _a.OwnerId.ToString(), StringComparison.Ordinal)}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var members = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/tenants/{_a.Slug}/members/{_a.OwnerId}"));
        Assert.Equal(HttpStatusCode.OK, members.StatusCode);
    }

    // B's own owner, naming A's owner in B's workspace: A's owner is not a member there.
    [Fact]
    public async Task B_cannot_remove_or_promote_As_owner_through_Bs_workspace()
    {
        var removed = await AsOwnerOfB(HttpMethod.Delete, $"/api/v1/studio/{_b.Slug}/members/{_a.OwnerId}");
        var promoted = await AsOwnerOfB(HttpMethod.Post, $"/api/v1/studio/{_b.Slug}/members/{_a.OwnerId}/ownership");

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (removed.StatusCode, promoted.StatusCode));
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

    // Staff invites (design §5.2): B's owner reaches none of A's invites through the studio routes.
    [Theory]
    [InlineData("GET", "/invites")]
    [InlineData("POST", "/invites")]
    [InlineData("DELETE", "/invites/{invite}")]
    public async Task B_cannot_reach_As_invites(string method, string path)
    {
        var email = TenancyArrange.EmailOf(Guid.CreateVersion7());
        await _api.Invite(_a, email);
        var invite = (await (await _api.AsOwner(_a, HttpMethod.Get, "/invites")).Content.ReadFromJsonAsync<List<InviteRow>>(TestContext.Current.CancellationToken))!
            .Single(i => i.Email == email);

        var request = new HttpRequestMessage(new HttpMethod(method), $"/api/v1/studio/{_a.Slug}{path.Replace("{invite}", invite.Id.ToString(), StringComparison.Ordinal)}");
        if (method == "POST") request.Content = JsonContent.Create(new { email = "hijack@test.local" });
        var response = await _api.SendAsGateway(request, _b.OwnerId, _b.TenantId, TenantRole.Owner);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains((await (await _api.AsOwner(_a, HttpMethod.Get, "/invites")).Content.ReadFromJsonAsync<List<InviteRow>>(TestContext.Current.CancellationToken))!, i => i.Id == invite.Id);
    }

    // The slug in the accept route only names where to look; the token decides. A's invite token
    // used with B's slug finds nothing, so it cannot make anyone staff of B.
    [Fact]
    public async Task An_invite_token_of_A_does_nothing_in_B()
    {
        var invitee = Guid.CreateVersion7();
        var token = await _api.Invite(_a, TenancyArrange.EmailOf(invitee));

        var response = await _api.Accept(_b.Slug, token, invitee);
        var inB = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/tenants/{_b.Slug}/members/{invitee}"));

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (response.StatusCode, inB.StatusCode));
    }

    // Platform admin routes (design §1, §3.4) answer only to the Entra app role. B's owner, even with
    // the gateway's tenant headers for B, is refused for any workspace (403 for every id, so nothing
    // about A leaks), and A is unchanged.
    [Theory]
    [InlineData("/status", "{\"status\":\"Suspended\"}")]
    [InlineData("/plan", "{\"plan\":\"Pro\"}")]
    public async Task B_cannot_suspend_A_or_change_As_plan(string path, string body)
    {
        var response = await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Put, $"/api/v1/admin/tenants/{_a.TenantId}{path}")
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            },
            _b.OwnerId, _b.TenantId, TenantRole.Owner);
        var storefront = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/coaches/{_a.Slug}"));
        var plan = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/tenants/{_a.Slug}/plan"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, storefront.StatusCode);
        Assert.Contains("\"name\":\"Free\"", await plan.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    // The admin's write enters the one tenant it names, so RLS lets it reach that row only: suspending
    // A leaves B Active.
    [Fact]
    public async Task Suspending_A_leaves_B_active()
    {
        var response = await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Put, $"/api/v1/admin/tenants/{_a.TenantId}/status") { Content = JsonContent.Create(new { status = "Suspended" }) },
            Guid.CreateVersion7(), appRoles: ["Platform.Admin"]);
        var aStorefront = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/coaches/{_a.Slug}"));
        var bStorefront = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/coaches/{_b.Slug}"));

        Assert.Equal(
            (HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.OK),
            (response.StatusCode, aStorefront.StatusCode, bStorefront.StatusCode));
    }

    private sealed record InviteRow(Guid Id, string Email);

    private Task<HttpResponseMessage> AsOwnerOfB(HttpMethod method, string path) =>
        _api.SendAsGateway(new HttpRequestMessage(method, path), _b.OwnerId, _b.TenantId, TenantRole.Owner);

    public ValueTask DisposeAsync() => _api.DisposeAsync();
}
