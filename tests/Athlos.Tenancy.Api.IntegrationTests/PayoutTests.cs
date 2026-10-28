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

    // Found on dev: Stripe refuses to create accounts until Connect is enabled on the platform account.
    [Fact]
    public async Task A_Stripe_refusal_is_a_502_with_a_message_the_studio_can_show()
    {
        var workspace = await _api.CreateWorkspace();
        _stripe.Failure = new Stripe.StripeException("You can only create new accounts if you've signed up for Connect.");

        var response = await AsOwner(workspace, HttpMethod.Post, "/payouts/onboarding");
        var problem = await response.Content.ReadFromJsonAsync<Problem>(Cancel);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("Stripe could not handle the payout request. Please try again later.", problem!.Title);
    }

    // Stripe replays the stored response of an idempotency key for 24 hours, errors included.
    [Fact]
    public void The_account_idempotency_key_absorbs_double_clicks_but_not_later_retries()
    {
        var tenant = Guid.CreateVersion7();
        var at = new DateTimeOffset(2026, 10, 3, 10, 34, 5, TimeSpan.Zero);

        var first = Athlos.Tenancy.Api.Features.Payouts.StripeConnect.AccountIdempotencyKey(tenant, at);
        var doubleClick = Athlos.Tenancy.Api.Features.Payouts.StripeConnect.AccountIdempotencyKey(tenant, at.AddSeconds(20));
        var retry = Athlos.Tenancy.Api.Features.Payouts.StripeConnect.AccountIdempotencyKey(tenant, at.AddMinutes(1));

        Assert.Equal(first, doubleClick);
        Assert.NotEqual(first, retry);
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

    // Stripe's account.updated (Connect endpoint) keeps "ready to sell" in step without the coach visiting
    // the studio: a restricted account stops selling, a fixed one sells again.
    [Fact]
    public async Task Stripe_account_updates_change_whether_the_workspace_can_sell()
    {
        var workspace = await _api.CreateWorkspace();
        await AsOwner(workspace, HttpMethod.Post, "/payouts/onboarding");
        var account = FakeStripeConnect.AccountOf(workspace.TenantId);

        _stripe.CompleteOnboarding(workspace.TenantId);
        var ready = await AccountUpdated(account);
        var readyAfterwards = (await PayoutAccountOf(workspace)).OnboardingComplete;
        _stripe.Restrict(workspace.TenantId);
        await AccountUpdated(account);
        var restricted = (await PayoutAccountOf(workspace)).OnboardingComplete;

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal((true, false), (readyAfterwards, restricted));
    }

    // Every stage shares one Stripe test account: an account no workspace here holds is ignored.
    [Fact]
    public async Task Connect_webhooks_ignore_unknown_accounts_and_refuse_bad_signatures()
    {
        var unknown = await AccountUpdated("acct_of_another_stage");
        var payload = StripeEvents.AccountUpdated($"evt_{Guid.NewGuid():N}", "acct_x");
        var forged = await ConnectWebhook(payload, StripeEvents.Signature(payload, secret: "whsec_not_the_secret"));
        var unsigned = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe-connect") { Content = new StringContent(payload) });

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (unknown.StatusCode, forged.StatusCode, unsigned.StatusCode));
    }

    private Task<HttpResponseMessage> AccountUpdated(string accountId)
    {
        var payload = StripeEvents.AccountUpdated($"evt_{Guid.NewGuid():N}", accountId);
        return ConnectWebhook(payload, StripeEvents.Signature(payload));
    }

    private Task<HttpResponseMessage> ConnectWebhook(string payload, string signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe-connect") { Content = new StringContent(payload) };
        request.Headers.Add("Stripe-Signature", signature);
        return _api.SendAsGateway(request);
    }

    private async Task<PayoutAccount> PayoutAccountOf(TenancyArrange.Workspace workspace) =>
        (await (await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/tenants/{workspace.TenantId}/payout-account")))
            .Content.ReadFromJsonAsync<PayoutAccount>(Cancel))!;

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

    // Payouts in the studio (backlog item 42): once Stripe has the coach's details, the owner opens the
    // Express dashboard and sees the balance and the latest payouts to the bank.
    [Fact]
    public async Task After_onboarding_the_owner_opens_the_Stripe_dashboard_and_sees_the_payouts()
    {
        var workspace = await _api.CreateWorkspace();
        await AsOwner(workspace, HttpMethod.Post, "/payouts/onboarding");
        _stripe.CompleteOnboarding(workspace.TenantId);
        await AsOwner(workspace, HttpMethod.Get, "/payouts");

        var dashboard = await AsOwner(workspace, HttpMethod.Post, "/payouts/dashboard");
        var history = await AsOwner(workspace, HttpMethod.Get, "/payouts/history");
        var payouts = await history.Content.ReadFromJsonAsync<History>(Cancel);

        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Equal($"https://connect.stripe.test/express/{FakeStripeConnect.AccountOf(workspace.TenantId)}", (await dashboard.Content.ReadFromJsonAsync<Link>(Cancel))!.Url);
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        Assert.Equal([new Money(4410, "EUR")], payouts!.Available);
        Assert.Equal([8820L, 4410L], payouts.Payouts.Select(p => p.Amount));
    }

    [Fact]
    public async Task Before_onboarding_there_is_no_dashboard_and_staff_never_see_the_payouts()
    {
        var workspace = await _api.CreateWorkspace();
        var staff = await _databases.AddMember(workspace.TenantId, TenantRole.Staff);
        await AsOwner(workspace, HttpMethod.Post, "/payouts/onboarding");

        var notYet = await AsOwner(workspace, HttpMethod.Post, "/payouts/dashboard");
        var noHistory = await AsOwner(workspace, HttpMethod.Get, "/payouts/history");
        _stripe.CompleteOnboarding(workspace.TenantId);
        await AsOwner(workspace, HttpMethod.Get, "/payouts");
        var asStaff = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/studio/{workspace.Slug}/payouts/history"), staff, workspace.TenantId, TenantRole.Staff);
        var staffDashboard = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, $"/api/v1/studio/{workspace.Slug}/payouts/dashboard"), staff, workspace.TenantId, TenantRole.Staff);

        Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.Conflict), (notYet.StatusCode, noHistory.StatusCode));
        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (asStaff.StatusCode, staffDashboard.StatusCode));
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

    private sealed record Money(long Amount, string Currency);

    private sealed record Payout(long Amount, string Currency, DateTimeOffset ArrivalDate, string Status);

    private sealed record History(List<Money> Available, List<Money> Pending, List<Payout> Payouts);

    private sealed record Status(bool Connected, bool OnboardingComplete, bool PayoutsEnabled);

    private sealed record PayoutAccount(Guid TenantId, string Slug, bool Active, string? StripeAccountId, bool OnboardingComplete);

    private sealed record Problem(string Title, int Status);
}
