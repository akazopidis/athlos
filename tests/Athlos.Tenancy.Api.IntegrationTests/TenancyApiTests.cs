using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Athlos.Tenancy;
using Athlos.Testing;

namespace Athlos.Tenancy.Api.IntegrationTests;

public sealed class TenancyApiTests(AthlosDatabases database) : IAsyncLifetime
{
    private readonly TenancyApiFactory _api = new(database);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Creating_a_workspace_makes_the_caller_its_owner()
    {
        var workspace = await _api.CreateWorkspace();

        var memberships = await Get<List<Membership>>("/api/v1/me/memberships", workspace.OwnerId);

        var membership = Assert.Single(memberships);
        Assert.Equal(workspace.TenantId, membership.TenantId);
        Assert.Equal(workspace.Slug, membership.Slug);
        Assert.Equal(TenantRole.Owner, membership.Role);
    }

    [Fact]
    public async Task A_taken_slug_is_a_conflict()
    {
        var existing = await _api.CreateWorkspace();

        var response = await CreateTenant(existing.Slug, Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("admin", HttpStatusCode.Conflict)]
    [InlineData("demo-forge", HttpStatusCode.Conflict)]
    [InlineData("Not A Slug", HttpStatusCode.BadRequest)]
    [InlineData("-leading-dash", HttpStatusCode.BadRequest)]
    [InlineData("ab", HttpStatusCode.BadRequest)]
    public async Task Reserved_and_malformed_slugs_are_rejected(string slug, HttpStatusCode expected)
    {
        var response = await CreateTenant(slug, Guid.CreateVersion7());

        Assert.Equal(expected, response.StatusCode);
    }

    // Opening a workspace accepts the Terms for coaches (design §5.4): without the current version
    // nothing is created; with it, the workspace keeps the version, the time and who accepted it.
    [Fact]
    public async Task Opening_a_workspace_requires_the_current_terms_for_coaches_and_records_them()
    {
        var owner = Guid.CreateVersion7();
        var slug = TenancyArrange.NewSlug();
        Task<HttpResponseMessage> Open(string? termsVersion) => _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenants") { Content = JsonContent.Create(new { slug, displayName = "Coach", termsVersion }) }, owner);

        var without = await Open(null);
        var outdated = await Open("2020-01-01");
        var accepted = await Open(TenancyArrange.CoachTermsVersion);

        Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.Conflict, HttpStatusCode.Created), (without.StatusCode, outdated.StatusCode, accepted.StatusCode));
        Assert.Contains("\"reason\":\"terms\"", await without.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        // The owner's data export shows what they accepted (design §5.4).
        var export = await (await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, "/api/v1/me/export"), owner)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains($"\"coachTermsVersion\":\"{TenancyArrange.CoachTermsVersion}\"", export, StringComparison.Ordinal);
        Assert.Contains("\"coachTermsAcceptedAt\":\"20", export, StringComparison.Ordinal);
    }

    // New Terms for coaches (design §5.4): the studio shows that the workspace has not accepted them;
    // only the owner accepts, only the version in force, and the workspace then counts as current.
    [Fact]
    public async Task The_owner_accepts_new_terms_for_coaches_for_an_existing_workspace()
    {
        var workspace = await _api.CreateWorkspace();
        await using var later = new TenancyApiFactory(database);
        later.Settings["Legal:CoachTermsVersion"] = "2026-12-01";
        Task<HttpResponseMessage> Accept(string version, TenantRole role) => later.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Post, $"/api/v1/studio/{workspace.Slug}/workspace/coach-terms") { Content = JsonContent.Create(new { termsVersion = version }) },
            workspace.OwnerId, workspace.TenantId, role);
        async Task<JsonElement> Read() => await (await later.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/studio/{workspace.Slug}/workspace"),
            workspace.OwnerId, workspace.TenantId, TenantRole.Owner)).Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        var before = await Read();
        var asStaff = await Accept("2026-12-01", TenantRole.Staff);
        var old = await Accept(TenancyArrange.CoachTermsVersion, TenantRole.Owner);
        var accepted = await Accept("2026-12-01", TenantRole.Owner);
        var after = await Read();

        Assert.Equal((TenancyArrange.CoachTermsVersion, false), (before.GetProperty("coachTermsVersion").GetString(), before.GetProperty("coachTermsCurrent").GetBoolean()));
        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Conflict, HttpStatusCode.NoContent), (asStaff.StatusCode, old.StatusCode, accepted.StatusCode));
        Assert.Equal(("2026-12-01", true), (after.GetProperty("coachTermsVersion").GetString(), after.GetProperty("coachTermsCurrent").GetBoolean()));
    }

    [Fact]
    public async Task Creating_a_workspace_requires_sign_in()
    {
        var response = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenants")
        {
            Content = JsonContent.Create(new { slug = TenancyArrange.NewSlug(), displayName = "Anonymous" }),
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_owner_reads_and_updates_the_workspace()
    {
        var workspace = await _api.CreateWorkspace();

        var update = await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Put, $"/api/v1/studio/{workspace.Slug}/workspace")
            {
                Content = JsonContent.Create(new
                {
                    displayName = "Strong Coaching", logoUrl = "https://example.com/logo.png", primaryColor = "#1a2b3c", contactEmail = "hello@strong.test",
                }),
            },
            workspace.OwnerId, workspace.TenantId, TenantRole.Owner);
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);

        var read = await Get<WorkspaceSettings>($"/api/v1/studio/{workspace.Slug}/workspace", workspace.OwnerId, workspace.TenantId, TenantRole.Owner);
        Assert.Equal("Strong Coaching", read.DisplayName);
        Assert.Equal(("#1a2b3c", "hello@strong.test"), (read.Branding.PrimaryColor, read.Branding.ContactEmail));
        var storefront = await (await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/coaches?slug={workspace.Slug}")))
            .Content.ReadFromJsonAsync<List<Storefront>>(Json, Cancel);
        Assert.Equal("hello@strong.test", Assert.Single(storefront!).Branding.ContactEmail);
    }

    [Fact]
    public async Task A_contact_address_that_is_not_an_email_is_refused()
    {
        var workspace = await _api.CreateWorkspace();

        var update = await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Put, $"/api/v1/studio/{workspace.Slug}/workspace")
            {
                Content = JsonContent.Create(new { displayName = "Strong Coaching", contactEmail = "not an address" }),
            },
            workspace.OwnerId, workspace.TenantId, TenantRole.Owner);

        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
    }

    [Fact]
    public async Task Staff_can_read_but_not_change_the_workspace()
    {
        var workspace = await _api.CreateWorkspace();
        var staff = await database.AddMember(workspace.TenantId, TenantRole.Staff);

        var read = await Send(HttpMethod.Get, $"/api/v1/studio/{workspace.Slug}/workspace", staff, workspace.TenantId, TenantRole.Staff);
        var update = await _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Put, $"/api/v1/studio/{workspace.Slug}/workspace")
            {
                Content = JsonContent.Create(new { displayName = "Taken over" }),
            },
            staff, workspace.TenantId, TenantRole.Staff);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
    }

    [Fact]
    public async Task The_storefront_is_public()
    {
        var workspace = await _api.CreateWorkspace();

        var storefront = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/coaches/{workspace.Slug}"));
        var unknown = await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, "/api/v1/marketplace/coaches/nobody-here"));

        Assert.Equal(HttpStatusCode.OK, storefront.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task The_profile_comes_from_the_token()
    {
        var userId = Guid.CreateVersion7();

        var profile = await Send(HttpMethod.Put, "/api/v1/me/profile", userId);
        var body = await profile.Content.ReadFromJsonAsync<Profile>(Json, Cancel);

        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        Assert.Equal($"{userId}@test.local", body!.Email);
    }

    [Fact]
    public async Task The_gateway_lookup_returns_the_members_role()
    {
        var workspace = await _api.CreateWorkspace();
        var staff = await database.AddMember(workspace.TenantId, TenantRole.Staff);

        var owner = await Get<Lookup>($"/internal/tenants/{workspace.Slug}/members/{workspace.OwnerId}");
        var staffLookup = await Get<Lookup>($"/internal/tenants/{workspace.Slug}/members/{staff}");
        var stranger = await Send(HttpMethod.Get, $"/internal/tenants/{workspace.Slug}/members/{Guid.CreateVersion7()}");

        Assert.Equal((workspace.TenantId, TenantRole.Owner), (owner.TenantId, owner.Role));
        Assert.Equal(TenantRole.Staff, staffLookup.Role);
        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
    }

    private Task<HttpResponseMessage> CreateTenant(string slug, Guid userId) =>
        _api.SendAsGateway(
            new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenants")
            {
                Content = JsonContent.Create(new { slug, displayName = "Coach", termsVersion = TenancyArrange.CoachTermsVersion }),
            },
            userId);

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, Guid? userId = null, Guid? tenantId = null, TenantRole? role = null) =>
        _api.SendAsGateway(new HttpRequestMessage(method, path), userId, tenantId, role);

    private async Task<T> Get<T>(string path, Guid? userId = null, Guid? tenantId = null, TenantRole? role = null)
    {
        var response = await Send(HttpMethod.Get, path, userId, tenantId, role);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(Json, Cancel))!;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    private sealed record Membership(Guid TenantId, string Slug, string DisplayName, TenantRole Role);

    private sealed record Branding(string? LogoUrl, string? PrimaryColor, string? ContactEmail);

    private sealed record Storefront(string Slug, string DisplayName, Branding Branding);

    private sealed record WorkspaceSettings(Guid Id, string Slug, string DisplayName, Branding Branding);

    private sealed record Profile(string Email, string DisplayName);

    private sealed record Lookup(Guid TenantId, TenantRole Role);
}
