using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Athlos.Testing;
using Npgsql;
using static Athlos.Testing.TenancyArrange;

namespace Athlos.Tenancy.Api.IntegrationTests;

// The platform admin (design §1, §3.4): an Entra app role, not a membership. It lists every workspace,
// suspends and reactivates them, and assigns their plan. Suspending closes the storefront and the
// studio and asks catalog-api to take the programs off the marketplace.
public sealed class PlatformAdminTests(AthlosDatabases databases) : IAsyncLifetime
{
    private static readonly string[] AdminRole = ["Platform.Admin"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TenancyApiFactory _api = new(databases);
    private readonly Guid _admin = Guid.CreateVersion7();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> AdminEndpoints => new()
    {
        { "GET", "/api/v1/admin/tenants" },
        { "PUT", "/api/v1/admin/tenants/{id}/status" },
        { "PUT", "/api/v1/admin/tenants/{id}/plan" },
        { "GET", "/api/v1/admin/metrics" },
    };

    [Theory]
    [MemberData(nameof(AdminEndpoints))]
    public async Task Coaches_and_customers_are_forbidden_and_anonymous_callers_unauthorized(string method, string path)
    {
        var workspace = await _api.CreateWorkspace();
        var body = method == "PUT" ? JsonContent.Create(new { status = "Suspended", plan = "Pro" }) : null;
        var url = path.Replace("{id}", workspace.TenantId.ToString(), StringComparison.Ordinal);

        var owner = await _api.SendAsGateway(new HttpRequestMessage(new HttpMethod(method), url) { Content = body }, workspace.OwnerId);
        var anonymous = await _api.SendAsGateway(new HttpRequestMessage(new HttpMethod(method), url) { Content = body });
        var otherRole = await _api.SendAsGateway(
            new HttpRequestMessage(new HttpMethod(method), url) { Content = body }, Guid.CreateVersion7(), appRoles: ["Platform.Reader"]);

        Assert.Equal(
            (HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden),
            (owner.StatusCode, anonymous.StatusCode, otherRole.StatusCode));
        Assert.Equal(("Active", "Free"), await StatusAndPlan(workspace.TenantId));
        Assert.False(_api.Suspension.Suspended.ContainsKey(workspace.TenantId));
    }

    [Fact]
    public async Task Suspending_closes_the_storefront_and_studio_and_reactivating_opens_them()
    {
        var workspace = await _api.CreateWorkspace();

        var suspended = await SetStatus(workspace.TenantId, "Suspended");

        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);
        Assert.True(_api.Suspension.Suspended[workspace.TenantId]);
        Assert.Equal("Suspended", (await StatusAndPlan(workspace.TenantId)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Storefront(workspace)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Membership(workspace)).StatusCode);
        Assert.Equal("[]", await (await Storefronts(workspace.Slug)).Content.ReadAsStringAsync(Cancel));
        var payout = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/tenants/{workspace.TenantId}/payout-account"));
        Assert.False((await payout.Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("active").GetBoolean());

        // The coach still sees the workspace in their list, marked Suspended, so the app can say why.
        var memberships = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, "/api/v1/me/memberships"), workspace.OwnerId);
        Assert.Contains("\"status\":\"Suspended\"", await memberships.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);

        var reactivated = await SetStatus(workspace.TenantId, "Active");

        Assert.Equal(HttpStatusCode.OK, reactivated.StatusCode);
        Assert.False(_api.Suspension.Suspended[workspace.TenantId]);
        Assert.Equal(HttpStatusCode.OK, (await Storefront(workspace)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Membership(workspace)).StatusCode);
    }

    [Fact]
    public async Task If_catalog_cannot_hide_the_programs_nothing_changes()
    {
        var workspace = await _api.CreateWorkspace();
        _api.Suspension.FailFor(workspace.TenantId);

        var response = await SetStatus(workspace.TenantId, "Suspended");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("Active", (await StatusAndPlan(workspace.TenantId)).Status);
    }

    [Fact]
    public async Task Deleting_is_not_a_status_the_admin_sets_and_deleted_workspaces_are_not_found()
    {
        var workspace = await _api.CreateWorkspace();
        var deleted = await _api.CreateWorkspace();
        Assert.Equal(HttpStatusCode.NoContent, (await _api.AsOwner(deleted, HttpMethod.Delete, "/workspace", new { confirmSlug = deleted.Slug })).StatusCode);

        var asDeleted = await SetStatus(workspace.TenantId, "Deleted");
        var suspendDeleted = await SetStatus(deleted.TenantId, "Suspended");
        var planDeleted = await SetPlan(deleted.TenantId, "Pro");
        var unknown = await SetPlan(Guid.CreateVersion7(), "Pro");

        Assert.Equal(
            (HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound),
            (asDeleted.StatusCode, suspendDeleted.StatusCode, planDeleted.StatusCode, unknown.StatusCode));
        Assert.Equal("Active", (await StatusAndPlan(workspace.TenantId)).Status);
    }

    [Fact]
    public async Task Assigning_Pro_raises_the_limits_the_gateway_and_catalog_read()
    {
        var workspace = await _api.CreateWorkspace();

        var response = await SetPlan(workspace.TenantId, "Pro");
        var plan = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/tenants/{workspace.Slug}/plan"));
        var limits = (await plan.Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("plan");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(("Active", "Pro"), await StatusAndPlan(workspace.TenantId));
        Assert.Equal(("Pro", 100), (limits.GetProperty("name").GetString(), limits.GetProperty("maxPrograms").GetInt32()));
    }

    [Fact]
    public async Task The_admin_finds_workspaces_by_address_or_status_and_sees_the_counts()
    {
        var workspace = await _api.CreateWorkspace();
        Assert.Equal(HttpStatusCode.OK, (await SetStatus(workspace.TenantId, "Suspended")).StatusCode);

        var byAddress = await AsAdmin<TenantPage>($"/api/v1/admin/tenants?q={workspace.Slug}");
        var suspended = await AsAdmin<TenantPage>("/api/v1/admin/tenants?status=Suspended&pageSize=50");
        var metrics = await AsAdmin<JsonElement>("/api/v1/admin/metrics");

        var item = Assert.Single(byAddress.Items);
        Assert.Equal((workspace.TenantId, "Suspended", "Free", false), (item.Id, item.Status, item.Plan, item.ReadyToSell));
        Assert.All(suspended.Items, i => Assert.Equal("Suspended", i.Status));
        Assert.True(suspended.TotalCount >= 1);
        Assert.True(metrics.GetProperty("suspended").GetInt32() >= 1);
        Assert.True(metrics.GetProperty("newLast30Days").GetInt32() >= 1);
        Assert.Equal(
            metrics.GetProperty("workspaces").GetInt32(),
            metrics.GetProperty("active").GetInt32() + metrics.GetProperty("suspended").GetInt32() + metrics.GetProperty("deleted").GetInt32());
    }

    private Task<HttpResponseMessage> SetStatus(Guid tenantId, string status) =>
        _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Put, $"/api/v1/admin/tenants/{tenantId}/status") { Content = JsonContent.Create(new { status }) },
            _admin, appRoles: AdminRole);

    private Task<HttpResponseMessage> SetPlan(Guid tenantId, string plan) =>
        _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Put, $"/api/v1/admin/tenants/{tenantId}/plan") { Content = JsonContent.Create(new { plan }) },
            _admin, appRoles: AdminRole);

    private async Task<T> AsAdmin<T>(string path)
    {
        var response = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, path), _admin, appRoles: AdminRole);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(Json, Cancel))!;
    }

    private Task<HttpResponseMessage> Storefront(Workspace workspace) =>
        _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/coaches/{workspace.Slug}"));

    // The shop's coach lookup lists active workspaces only.
    [Fact]
    public async Task The_coach_lookup_lists_active_workspaces_by_address()
    {
        var (anna, ben, hidden) = (await _api.CreateWorkspace(), await _api.CreateWorkspace(), await _api.CreateWorkspace());
        Assert.Equal(HttpStatusCode.OK, (await SetStatus(hidden.TenantId, "Suspended")).StatusCode);

        var found = await (await Storefronts(anna.Slug, ben.Slug, hidden.Slug, "no-such-coach")).Content.ReadFromJsonAsync<List<JsonElement>>(Cancel);
        var tooMany = await Storefronts(Enumerable.Range(0, 51).Select(i => $"coach-{i}").ToArray());

        Assert.Equivalent(new[] { anna.Slug, ben.Slug }, found!.Select(f => f.GetProperty("slug").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    private Task<HttpResponseMessage> Storefronts(params string[] slugs) =>
        _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, "/api/v1/marketplace/coaches?" + string.Join('&', slugs.Select(s => $"slug={s}"))));

    private Task<HttpResponseMessage> Membership(Workspace workspace) =>
        _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/internal/tenants/{workspace.Slug}/members/{workspace.OwnerId}"));

    // As the superuser, so RLS cannot hide anything.
    private async Task<(string Status, string Plan)> StatusAndPlan(Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(databases.OwnerConnectionString("tenancy"));
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand("SELECT status, plan FROM tenants WHERE id = @tenant", connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        await reader.ReadAsync(Cancel);
        return (reader.GetString(0), reader.GetString(1));
    }

    private sealed record TenantPage(List<TenantItem> Items, int Page, int PageSize, int TotalCount);

    private sealed record TenantItem(Guid Id, string Slug, string Status, string Plan, bool ReadyToSell);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();
}
