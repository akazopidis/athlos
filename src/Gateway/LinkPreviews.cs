using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Text.RegularExpressions;
using Athlos.Tenancy;
using Microsoft.Extensions.Caching.Hybrid;

namespace Athlos.Gateway;

// Link previews for storefronts and program pages (design §8.1). Social apps and search engines read a
// page's HTML without running the SPA, so for /c/{slug} and /c/{slug}/programs/{programSlug} the gateway
// serves the SPA shell with that page's title, description and image filled in (Open Graph and Twitter
// card tags), read from the services' public marketplace endpoints, signed like any forwarded request.
// Anything that fails (an unknown coach, a service starting from zero) serves the plain shell: the SPA
// renders the page either way. Previews are cached for 5 minutes; an image link (a read link to a cover
// or logo, valid at least 10 minutes) is fetched by the app soon after it reads the page.
public sealed partial class LinkPreviews(IHttpClientFactory clients, GatewaySignature signature, HybridCache cache, IWebHostEnvironment environment, ILogger<LinkPreviews> logger)
{
    public const string Tenancy = "preview-tenancy";
    public const string Catalog = "preview-catalog";

    // Escapes markup (<, >, &, quotes) but keeps letters of every script, so Greek titles stay readable.
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

    private static readonly HybridCacheEntryOptions CacheFor = new() { Expiration = TimeSpan.FromMinutes(5), LocalCacheExpiration = TimeSpan.FromMinutes(5) };

    public sealed record Preview(string Title, string Description, string? Image);

    private sealed record Storefront(string DisplayName, Profile Branding);

    private sealed record Profile(string? LogoUrl, string? Bio);

    private sealed record Program(string Title, string Description, string? CoverImageUrl, long PriceAmount, string Currency);

    public Task<IResult> StorefrontPageAsync(HttpContext context, string slug) =>
        PageAsync(context, $"storefront:{slug}", token => StorefrontAsync(slug, token));

    public Task<IResult> ProgramPageAsync(HttpContext context, string slug, string programSlug) =>
        PageAsync(context, $"program:{slug}/{programSlug}", token => ProgramAsync(slug, programSlug, token));

    private async Task<IResult> PageAsync(HttpContext context, string key, Func<CancellationToken, Task<Preview?>> find)
    {
        var shell = environment.WebRootFileProvider.GetFileInfo("index.html");
        if (!shell.Exists)
        {
            return Results.NotFound();
        }

        string html;
        using (var reader = new StreamReader(shell.CreateReadStream()))
        {
            html = await reader.ReadToEndAsync(context.RequestAborted);
        }

        // A preview never holds the page up for long: after 4 seconds (a service starting from zero, say)
        // the plain shell goes out, and the next request tries again (failures are not cached).
        Preview? preview = null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        limit.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            preview = await cache.GetOrCreateAsync(key, async token => await find(token), CacheFor, cancellationToken: limit.Token);
        }
        catch (Exception e) when (!context.RequestAborted.IsCancellationRequested)
        {
            LogPreviewFailed(logger, key, e);
        }

        context.Response.Headers.CacheControl = "no-cache";
        var url = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}";
        return Results.Content(preview is null ? html : Fill(html, preview, url), "text/html; charset=utf-8");
    }

    // The page's own <title> and description, plus the tags link previews read. Values are HTML-encoded.
    public static string Fill(string html, Preview preview, string url)
    {
        var e = Encoder;
        var title = $"{preview.Title} · Athlos";
        html = TitleTag().Replace(html, $"<title>{e.Encode(title)}</title>", 1);
        html = DescriptionTag().Replace(html, $"<meta name=\"description\" content=\"{e.Encode(preview.Description)}\" />", 1);
        var tags = string.Concat(
            $"<meta property=\"og:type\" content=\"website\" />",
            $"<meta property=\"og:site_name\" content=\"Athlos\" />",
            $"<meta property=\"og:title\" content=\"{e.Encode(title)}\" />",
            $"<meta property=\"og:description\" content=\"{e.Encode(preview.Description)}\" />",
            $"<meta property=\"og:url\" content=\"{e.Encode(url)}\" />",
            preview.Image is null ? "" : $"<meta property=\"og:image\" content=\"{e.Encode(preview.Image)}\" />",
            $"<meta name=\"twitter:card\" content=\"{(preview.Image is null ? "summary" : "summary_large_image")}\" />");
        var head = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        return head >= 0 ? html.Insert(head, tags) : tags + html;
    }

    private async Task<Preview?> StorefrontAsync(string slug, CancellationToken cancellationToken)
    {
        var storefront = await GetAsync<Storefront>(Tenancy, $"/api/v1/marketplace/coaches/{Uri.EscapeDataString(slug)}", cancellationToken);
        return storefront is null
            ? null
            : new Preview(storefront.DisplayName, Shorten(storefront.Branding.Bio ?? $"Training programs by {storefront.DisplayName} on Athlos."), storefront.Branding.LogoUrl);
    }

    private async Task<Preview?> ProgramAsync(string slug, string programSlug, CancellationToken cancellationToken)
    {
        var program = await GetAsync<Program>(Catalog,
            $"/api/v1/marketplace/coaches/{Uri.EscapeDataString(slug)}/programs/{Uri.EscapeDataString(programSlug)}", cancellationToken);
        return program is null ? null : new Preview(program.Title, Shorten(program.Description), program.CoverImageUrl);
    }

    private async Task<T?> GetAsync<T>(string client, string path, CancellationToken cancellationToken)
        where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        var signed = signature.Sign("GET", path, tenantId: null, userRole: null);
        request.Headers.Add(GatewaySignature.TimestampHeader, signed.Timestamp);
        request.Headers.Add(GatewaySignature.SignatureHeader, signed.Signature);
        using var response = await clients.CreateClient(client).SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<T>(cancellationToken) : null;
    }

    private static string Shorten(string text)
    {
        var line = string.Join(' ', text.Split((char[])['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length <= 200 ? line : line[..197].TrimEnd() + "…";
    }

    [GeneratedRegex("<title>.*?</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    [GeneratedRegex("<meta name=\"description\"[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex DescriptionTag();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Link preview {Key} could not be read; serving the plain page")]
    private static partial void LogPreviewFailed(ILogger logger, string key, Exception exception);
}

public static class LinkPreviewEndpoints
{
    // Before the SPA fallback: these two client-side routes get their preview tags.
    public static void MapLinkPreviews(this WebApplication app)
    {
        app.MapGet("/c/{slug}", (string slug, HttpContext context, LinkPreviews previews) => previews.StorefrontPageAsync(context, slug));
        app.MapGet("/c/{slug}/programs/{programSlug}", (string slug, string programSlug, HttpContext context, LinkPreviews previews) =>
            previews.ProgramPageAsync(context, slug, programSlug));
    }
}
