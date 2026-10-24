using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Athlos.Gateway.Tests;

// robots.txt and sitemap.xml (design §8.1): the private areas are disallowed, and the sitemap lists the
// static pages, every storefront with programs and every program page, read from catalog's search.
public sealed class SitemapTests : IAsyncLifetime
{
    private WebApplication? _backend;
    private WebApplicationFactory<MembershipLookup>? _gateway;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _backend = builder.Build();
        _backend.MapGet("/api/v1/marketplace/programs", (int page) => Results.Ok(new
        {
            items = page == 1
                ? new[] { new { coachSlug = "forge", slug = "strong-start" }, new { coachSlug = "forge", slug = "deadlift-peak" }, new { coachSlug = "flow", slug = "a&b" } }
                : [],
            totalCount = 3,
        }));
        await _backend.StartAsync(TestContext.Current.CancellationToken);
    }

    private HttpClient Gateway()
    {
        var backend = _backend!.Urls.First();
        _gateway ??= new GatewayFactory().WithWebHostBuilder(web => web.UseSetting("services:catalog-api:http:0", backend));
        return _gateway.CreateClient();
    }

    [Fact]
    public async Task The_sitemap_lists_storefronts_and_program_pages()
    {
        var response = await Gateway().GetAsync("/sitemap.xml", TestContext.Current.CancellationToken);
        var xml = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<loc>http://localhost/c/forge</loc>", xml, StringComparison.Ordinal);
        Assert.Contains("<loc>http://localhost/c/forge/programs/deadlift-peak</loc>", xml, StringComparison.Ordinal);
        Assert.Contains("<loc>http://localhost/c/flow/programs/a&amp;b</loc>", xml, StringComparison.Ordinal);
        Assert.Contains("<loc>http://localhost/coaches</loc>", xml, StringComparison.Ordinal);
        Assert.Single(xml.Split("/c/forge</loc>")[1..]);
    }

    [Fact]
    public async Task Robots_keeps_crawlers_out_of_private_areas_and_names_the_sitemap()
    {
        var robots = await Gateway().GetStringAsync("/robots.txt", TestContext.Current.CancellationToken);

        Assert.Contains("Disallow: /studio", robots, StringComparison.Ordinal);
        Assert.Contains("Disallow: /me", robots, StringComparison.Ordinal);
        Assert.Contains("Sitemap: http://localhost/sitemap.xml", robots, StringComparison.Ordinal);
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
