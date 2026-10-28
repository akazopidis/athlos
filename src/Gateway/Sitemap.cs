using System.Net.Http.Json;
using System.Security;
using System.Text;
using Athlos.Tenancy;
using Microsoft.Extensions.Caching.Hybrid;

namespace Athlos.Gateway;

// For search engines (design §8.1): /robots.txt keeps them out of the private areas (studio, account,
// cart, admin) and points at /sitemap.xml, which lists the shop's public pages: the home page, the
// coach directory, every storefront with programs on sale and every program page. The list is read
// from catalog-api's public marketplace search (signed, like any forwarded request) and cached for an
// hour; a failure serves the static pages only, so crawlers always get a valid file.
public sealed partial class Sitemap(IHttpClientFactory clients, GatewaySignature signature, HybridCache cache, ILogger<Sitemap> logger)
{
    // Up to 1,000 programs (20 pages of 50): far beyond the shop's size; a bigger shop would use a
    // sitemap index.
    private const int PageSize = 50;
    private const int MaxPages = 20;

    private static readonly HybridCacheEntryOptions CacheFor = new() { Expiration = TimeSpan.FromHours(1), LocalCacheExpiration = TimeSpan.FromHours(1) };

    private sealed record Item(string CoachSlug, string Slug);

    private sealed record Page(List<Item> Items, int TotalCount);

    public static string Robots(string origin) => $"""
        User-agent: *
        Allow: /
        Disallow: /studio
        Disallow: /me
        Disallow: /admin
        Disallow: /cart
        Disallow: /invites
        Disallow: /unsubscribe
        Sitemap: {origin}/sitemap.xml

        """;

    public async Task<IResult> XmlAsync(HttpContext context)
    {
        var origin = $"{context.Request.Scheme}://{context.Request.Host}";
        List<Item> programs = [];
        try
        {
            programs = await cache.GetOrCreateAsync("sitemap:programs", async token => await ProgramsAsync(token), CacheFor, cancellationToken: context.RequestAborted);
        }
        catch (Exception e) when (!context.RequestAborted.IsCancellationRequested)
        {
            LogSitemapFailed(logger, e);
        }

        var urls = new List<string> { "/", "/coaches", "/sell", "/legal/terms", "/legal/privacy", "/legal/refunds", "/legal/coaches" };
        urls.AddRange(programs.Select(p => p.CoachSlug).Distinct().Order().Select(slug => $"/c/{slug}"));
        urls.AddRange(programs.Select(p => $"/c/{p.CoachSlug}/programs/{p.Slug}"));

        var xml = new StringBuilder("""<?xml version="1.0" encoding="UTF-8"?>""").Append('\n')
            .Append("""<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">""").Append('\n');
        foreach (var url in urls)
        {
            xml.Append("  <url><loc>").Append(SecurityElement.Escape(origin + url)).Append("</loc></url>\n");
        }

        xml.Append("</urlset>\n");
        return Results.Content(xml.ToString(), "application/xml; charset=utf-8");
    }

    private async Task<List<Item>> ProgramsAsync(CancellationToken cancellationToken)
    {
        var all = new List<Item>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var path = $"/api/v1/marketplace/programs?Page={page}&PageSize={PageSize}&Sort=Newest";
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            var signed = signature.Sign("GET", path, tenantId: null, userRole: null);
            request.Headers.Add(GatewaySignature.TimestampHeader, signed.Timestamp);
            request.Headers.Add(GatewaySignature.SignatureHeader, signed.Signature);
            using var response = await clients.CreateClient(LinkPreviews.Catalog).SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<Page>(cancellationToken);
            all.AddRange(result?.Items ?? []);
            if (result is null || all.Count >= result.TotalCount || result.Items.Count == 0)
            {
                break;
            }
        }

        return all;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The sitemap's programs could not be read; serving the static pages only")]
    private static partial void LogSitemapFailed(ILogger logger, Exception exception);
}

public static class SitemapEndpoints
{
    // Before the SPA fallback, like the link previews.
    public static void MapSitemap(this WebApplication app)
    {
        app.MapGet("/robots.txt", (HttpContext context) =>
            Results.Text(Sitemap.Robots($"{context.Request.Scheme}://{context.Request.Host}"), "text/plain; charset=utf-8"));
        app.MapGet("/sitemap.xml", (HttpContext context, Sitemap sitemap) => sitemap.XmlAsync(context));
    }
}
