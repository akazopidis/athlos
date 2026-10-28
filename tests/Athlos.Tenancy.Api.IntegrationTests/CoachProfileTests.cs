using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Athlos.Tenancy;
using Athlos.Testing;

namespace Athlos.Tenancy.Api.IntegrationTests;

// The coach's profile (design §8): bio and links in the workspace settings, and an uploaded logo that
// storefronts show through a short-lived read link, in place of a linked one.
public sealed class CoachProfileTests(AthlosDatabases database, AthlosStorage storage) : IAsyncLifetime
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[56]];

    private TenancyApiFactory _api = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _api = new TenancyApiFactory(database, storage: storage);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task The_storefront_shows_the_bio_and_links()
    {
        var workspace = await _api.CreateWorkspace();

        var update = await _api.AsOwner(workspace, HttpMethod.Put, "/workspace", new
        {
            displayName = "Forge", bio = "Strength coach for ten years.", instagramUrl = "https://instagram.com/forge",
            youTubeUrl = "https://youtube.com/@forge", websiteUrl = "https://forge.example",
        });
        var notALink = await _api.AsOwner(workspace, HttpMethod.Put, "/workspace", new { displayName = "Forge", instagramUrl = "javascript:alert(1)" });
        // Link rules (backlog item 45): https only, for links and a linked logo.
        var plainHttp = await _api.AsOwner(workspace, HttpMethod.Put, "/workspace", new { displayName = "Forge", websiteUrl = "http://forge.example" });
        var httpLogo = await _api.AsOwner(workspace, HttpMethod.Put, "/workspace", new { displayName = "Forge", logoUrl = "http://forge.example/logo.png" });

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.BadRequest), (update.StatusCode, notALink.StatusCode));
        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (plainHttp.StatusCode, httpLogo.StatusCode));
        var profile = (await ReadStorefront(workspace.Slug)).Branding;
        Assert.Equal(("Strength coach for ten years.", "https://instagram.com/forge", "https://forge.example"), (profile.Bio, profile.InstagramUrl, profile.WebsiteUrl));
    }

    [Fact]
    public async Task An_uploaded_logo_replaces_the_linked_one_and_survives_a_settings_save()
    {
        var workspace = await _api.CreateWorkspace();
        await _api.AsOwner(workspace, HttpMethod.Put, "/workspace", new { displayName = "Forge", logoUrl = "https://example.com/logo.png" });

        var upload = await UploadLogo(workspace, Png, workspace.OwnerId, TenantRole.Owner);
        var uploaded = (await ReadStorefront(workspace.Slug)).Branding.LogoUrl;
        await _api.AsOwner(workspace, HttpMethod.Put, "/workspace", new { displayName = "Forge Strength", logoUrl = "https://example.com/logo.png" });
        var afterSave = (await ReadStorefront(workspace.Slug)).Branding.LogoUrl;
        var removed = await _api.AsOwner(workspace, HttpMethod.Delete, "/workspace/logo");
        var linkedAgain = (await ReadStorefront(workspace.Slug)).Branding.LogoUrl;

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (upload.StatusCode, removed.StatusCode));
        Assert.Contains($"tenants/{workspace.TenantId}/workspace/logo-", uploaded, StringComparison.Ordinal);
        Assert.Contains($"tenants/{workspace.TenantId}/workspace/logo-", afterSave, StringComparison.Ordinal);
        Assert.Equal("https://example.com/logo.png", linkedAgain);
    }

    [Fact]
    public async Task Only_the_owner_uploads_and_only_images_are_taken()
    {
        var workspace = await _api.CreateWorkspace();
        var staff = await database.AddMember(workspace.TenantId, TenantRole.Staff);

        var byStaff = await UploadLogo(workspace, Png, staff, TenantRole.Staff);
        var notAnImage = await UploadLogo(workspace, "not an image"u8.ToArray(), workspace.OwnerId, TenantRole.Owner);

        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.UnsupportedMediaType), (byStaff.StatusCode, notAnImage.StatusCode));
    }

    private Task<HttpResponseMessage> UploadLogo(TenancyArrange.Workspace workspace, byte[] image, Guid userId, TenantRole role)
    {
        var content = new ByteArrayContent(image);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Put, $"/api/v1/studio/{workspace.Slug}/workspace/logo") { Content = content },
            userId, workspace.TenantId, role);
    }

    private async Task<Storefront> ReadStorefront(string slug) =>
        (await (await _api.SendAsGateway(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/marketplace/coaches/{slug}")))
            .Content.ReadFromJsonAsync<Storefront>(Cancel))!;

    private sealed record Profile(string? LogoUrl, string? Bio, string? InstagramUrl, string? YouTubeUrl, string? WebsiteUrl);

    private sealed record Storefront(string Slug, string DisplayName, Profile Branding);

    public ValueTask DisposeAsync() => _api.DisposeAsync();
}
