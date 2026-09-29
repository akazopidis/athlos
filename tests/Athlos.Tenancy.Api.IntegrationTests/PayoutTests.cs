using System.Net;
using System.Net.Http.Json;
using Athlos.Tenancy;
using Athlos.Testing;

namespace Athlos.Tenancy.Api.IntegrationTests;

public sealed class PayoutTests : IAsyncLifetime
{
    private readonly AthlosDatabases _databases;
    private readonly FakeStripeConnect _stripe = new();
    private readonly TenancyApiFactory _api;

    public PayoutTests(AthlosDatabases databases)
    {
        _databases = databases;
        _api = new TenancyApiFactory(databases, _stripe);
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_owner_gets_an_onboarding_link_back_to_the_studio()
    {
        var workspace = await _api.CreateWorkspace();

        var response = await AsOwner(workspace, HttpMethod.Post, "/payouts/onboarding");
        var link = await response.Content.ReadFromJsonAsync<Link>(Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(Uri.EscapeDataString($"{TenancyApiFactory.PublicUrl}/studio/{workspace.Slug}?payouts=return"), link!.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Starting_onboarding_twice_reuses_the_Stripe_account()
    {
        var workspace = await _api.CreateWorkspace();

        await AsOwner(workspace, HttpMethod.Post, "/payouts/onboarding");
        await AsOwner(workspace, HttpMethod.Post, "/payouts/onboarding");

        Assert.Equal(1, _stripe.AccountsCreated);
    }

    [Fact]
    public async Task The_status_follows_Stripe_and_is_shared_with_other_services()
    {
        var workspace = await _api.CreateWorkspace();
        await AsOwner(workspace, HttpMethod.Post, "/payouts/onboarding");

        var before = await (await AsOwner(workspace, HttpMethod.Get, "/payouts")).Content.ReadFromJsonAsync<Status>(Cancel);
        _stripe.CompleteOnboarding(workspace.TenantId);
        var after = await (await AsOwner(workspace, HttpMethod.Get, "/payouts")).Content.ReadFromJsonAsync<Status>(Cancel);
        var forOrdering = await (await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/tenants/{workspace.TenantId}/payout-account")))
            .Content.ReadFromJsonAsync<PayoutAccount>(Cancel);

        Assert.Equal((true, false), (before!.Connected, before.OnboardingComplete));
        Assert.True(after!.OnboardingComplete);
        Assert.True(forOrdering!.OnboardingComplete);
        Assert.Equal($"acct_test_{workspace.TenantId:N}", forOrdering.StripeAccountId);
    }

    [Fact]
    public async Task Staff_can_see_payouts_but_not_set_them_up()
    {
        var workspace = await _api.CreateWorkspace();
        var staff = await _databases.AddMember(workspace.TenantId, TenantRole.Staff);

        var status = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/studio/{workspace.Slug}/payouts"), staff, workspace.TenantId, TenantRole.Staff);
        var start = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, $"/api/v1/studio/{workspace.Slug}/payouts/onboarding"), staff, workspace.TenantId, TenantRole.Staff);

        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);
    }

    [Fact]
    public async Task Without_a_Stripe_key_onboarding_is_unavailable_not_faked()
    {
        await using var unconfigured = new TenancyApiFactory(_databases);
        var workspace = await unconfigured.CreateWorkspace();

        var response = await unconfigured.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Post, $"/api/v1/studio/{workspace.Slug}/payouts/onboarding"),
            workspace.OwnerId, workspace.TenantId, TenantRole.Owner);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private Task<HttpResponseMessage> AsOwner(TenancyArrange.Workspace workspace, HttpMethod method, string path) =>
        _api.SendAsGateway(new HttpRequestMessage(method, $"/api/v1/studio/{workspace.Slug}{path}"), workspace.OwnerId, workspace.TenantId, TenantRole.Owner);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    private sealed record Link(string Url);

    private sealed record Status(bool Connected, bool OnboardingComplete, bool PayoutsEnabled);

    private sealed record PayoutAccount(Guid TenantId, string Slug, bool Active, string? StripeAccountId, bool OnboardingComplete);
}
