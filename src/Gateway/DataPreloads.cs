using System.Text.Encodings.Web;

namespace Athlos.Gateway;

// The public pages' first data requests, preloaded from the shell (design §8.1, backlog item 56). The
// SPA asks for them only once its JavaScript has loaded and run; named in index.html, the browser
// fetches them while the JavaScript downloads, and the SPA's own request is answered from that preload.
// A preload is used only if its URL is exactly the SPA's (path, parameter names and order), so these
// follow the requests the pages make; the gateway test pins them. Later requests depend on these
// answers (the coaches of the programs found, the program's reviews) and are not preloaded.
public static class DataPreloads
{
    // The first page of a catalog: Catalog.tsx's search with no filters, 12 per page.
    private const string FirstPage = "Page=1&PageSize=12";

    public static IReadOnlyList<string> For(PathString path, QueryString query)
    {
        var segments = path.Value?.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        return segments switch
        {
            // The shop: the coach facet (useMarketplaceCoaches) always, the first page when nothing is
            // searched or filtered (a query changes the SPA's URL).
            [] => query.HasValue
                ? ["/api/v1/marketplace/programs?PageSize=1"]
                : [$"/api/v1/marketplace/programs?{FirstPage}", "/api/v1/marketplace/programs?PageSize=1"],
            ["coaches"] => ["/api/v1/marketplace/programs?PageSize=1"],
            ["c", var slug] when !query.HasValue =>
                [$"/api/v1/marketplace/coaches/{Escape(slug)}", $"/api/v1/marketplace/programs?Coach={Escape(slug)}&{FirstPage}"],
            ["c", var slug] => [$"/api/v1/marketplace/coaches/{Escape(slug)}"],
            ["c", var slug, "programs", var program] =>
            [
                $"/api/v1/marketplace/coaches/{Escape(slug)}",
                $"/api/v1/marketplace/coaches/{Escape(slug)}/programs/{Escape(program)}",
                $"/api/v1/marketplace/programs?Coach={Escape(slug)}&PageSize=5",
            ],
            _ => [],
        };
    }

    // The preload tags, before </head>. as="fetch" with crossorigin matches the SPA's fetch (mode cors,
    // same-origin credentials), as the /config.json preload does.
    public static string Insert(string html, IReadOnlyList<string> urls)
    {
        if (urls.Count == 0)
        {
            return html;
        }

        var tags = string.Concat(urls.Select(url => $"<link rel=\"preload\" href=\"{HtmlEncoder.Default.Encode(url)}\" as=\"fetch\" crossorigin />"));
        var head = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        return head >= 0 ? html.Insert(head, tags) : html;
    }

    // The shop and the coaches page: the shell with their preloads (other client-side routes get the
    // plain shell from the SPA fallback; storefronts and program pages from LinkPreviews).
    public static void MapDataPreloads(this WebApplication app)
    {
        app.MapGet("/", (HttpContext context, IWebHostEnvironment environment) => ShellAsync(context, environment));
        app.MapGet("/coaches", (HttpContext context, IWebHostEnvironment environment) => ShellAsync(context, environment));
    }

    private static async Task<IResult> ShellAsync(HttpContext context, IWebHostEnvironment environment)
    {
        var shell = environment.WebRootFileProvider.GetFileInfo("index.html");
        if (!shell.Exists)
        {
            return Results.NotFound();
        }

        using var reader = new StreamReader(shell.CreateReadStream());
        var html = await reader.ReadToEndAsync(context.RequestAborted);
        context.Response.Headers.CacheControl = "no-cache";
        return Results.Content(Insert(html, For(context.Request.Path, context.Request.QueryString)), "text/html; charset=utf-8");
    }

    // The SPA's client (openapi-fetch) escapes path parameters with encodeURIComponent; addresses are
    // [a-z0-9-] anyway.
    private static string Escape(string segment) => Uri.EscapeDataString(segment);
}
