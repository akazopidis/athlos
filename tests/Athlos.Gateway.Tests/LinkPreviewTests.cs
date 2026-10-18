using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Athlos.Gateway.Tests;

// Link previews (design §8.1): a storefront's or program's address serves the SPA shell with that page's
// title, description and image, for apps that read HTML without running the SPA.
public sealed class LinkPreviewTests : IAsyncLifetime
{
    private WebApplication? _backend;
    private WebApplicationFactory<MembershipLookup>? _gateway;

    public async ValueTask InitializeAsync()
    {
        // A stand-in for tenancy-api and catalog-api's public marketplace reads.
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _backend = builder.Build();
        _backend.MapGet("/api/v1/marketplace/coaches/{slug}", (string slug) => slug == "forge"
            ? Results.Ok(new { slug, displayName = "Forge <Strength>", branding = new { logoUrl = "https://media.test/logo.png", bio = "Strength coach.\nTen years." } })
            : Results.NotFound());
        _backend.MapGet("/api/v1/marketplace/coaches/{slug}/programs/{program}", () =>
            Results.Ok(new { title = "Strong Start", description = "Learn the big lifts.", coverImageUrl = (string?)null, priceAmount = 4900, currency = "EUR" }));
        await _backend.StartAsync(TestContext.Current.CancellationToken);
    }

    private HttpClient Gateway()
    {
        var backend = _backend!.Urls.First();
        _gateway ??= new GatewayFactory().WithWebHostBuilder(web =>
            web.UseSetting("services:tenancy-api:http:0", backend).UseSetting("services:catalog-api:http:0", backend));
        return _gateway.CreateClient();
    }

    [Fact]
    public void Preview_tags_are_filled_in_and_encoded()
    {
        var html = LinkPreviews.Fill(
            "<html><head><meta name=\"description\" content=\"Generic.\" /><title>Athlos</title></head><body></body></html>",
            new LinkPreviews.Preview("Forge <script>", "About \"Forge\"", "https://media.test/a.png"), "https://athlos.test/c/forge");

        Assert.Contains("<title>Forge &lt;script&gt; · Athlos</title>", html, StringComparison.Ordinal);
        Assert.Contains("<meta name=\"description\" content=\"About &quot;Forge&quot;\" />", html, StringComparison.Ordinal);
        Assert.Contains("<meta property=\"og:image\" content=\"https://media.test/a.png\" />", html, StringComparison.Ordinal);
        Assert.Contains("summary_large_image", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Generic.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_storefront_address_serves_the_shell_with_its_preview()
    {
        var response = await Gateway().GetAsync("/c/forge", TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("og:title\" content=\"Forge &lt;Strength&gt; · Athlos\"", html, StringComparison.Ordinal);
        Assert.Contains("og:description\" content=\"Strength coach. Ten years.\"", html, StringComparison.Ordinal);
        Assert.Contains("og:image\" content=\"https://media.test/logo.png\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_program_address_gets_the_programs_preview_and_an_unknown_coach_the_plain_shell()
    {
        var program = await (await Gateway().GetAsync("/c/forge/programs/strong-start", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var unknown = await Gateway().GetAsync("/c/nobody", TestContext.Current.CancellationToken);

        Assert.Contains("<title>Strong Start · Athlos</title>", program, StringComparison.Ordinal);
        Assert.Contains("twitter:card\" content=\"summary\"", program, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.DoesNotContain("og:title", await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        if (_gateway is not null)
        {
            await _gateway.DisposeAsync();
        }

        if (_backend is not null)
        {
            await _backend.DisposeAsync();
        }
    }
}
