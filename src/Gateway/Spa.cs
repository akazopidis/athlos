namespace Athlos.Gateway;

// The gateway serves the SPA's Vite build from wwwroot (design §8.1).
public static class Spa
{
    // /internal is service-to-service only and never routed by the gateway (design §7.1).
    // /assets is the SPA's build: a file that is not there (an old tab asking for last release's
    // chunk) is a 404, not index.html served as JavaScript.
    private static readonly string[] ServerPrefixes = ["/api", "/webhooks", "/internal", "/health", "/assets"];

    // Vite names every file under /assets after a hash of its content, so a name never changes
    // content: browsers keep them for a year without asking again. index.html is no-cache, so the
    // next visit after a deploy loads the new names.
    private const string ImmutableAsset = "public, max-age=31536000, immutable";

    // Per-environment SPA settings, read at runtime so one image runs in every environment.
    // RefundWindowDays: shown to customers ("refundable within 14 days"); Payments:RefundWindowDays, the
    // same setting ordering-api enforces (default 14), so set it on both apps when it changes. Likewise
    // PlatformFeePercent (Payments:PlatformFeePercent, default 10), shown on the "Sell on Athlos" page.
    public sealed record SpaConfig(string? EntraClientId, string? EntraAuthority, string? ApiScope, string Version, int RefundWindowDays, int PlatformFeePercent);

    public static IEndpointRouteBuilder MapSpaConfig(this WebApplication app)
    {
        var section = app.Configuration.GetSection("Spa");
        var config = new SpaConfig(
            section["EntraClientId"],
            section["EntraAuthority"],
            section["ApiScope"],
            app.Configuration.GetAppVersion(),
            app.Configuration.GetValue("Payments:RefundWindowDays", 14),
            app.Configuration.GetValue("Payments:PlatformFeePercent", 10));

        // no-cache rather than no-store: index.html preloads it, and the browser drops a no-store preload.
        app.MapGet("/config.json", (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-cache";
                return TypedResults.Ok(config);
            })
            .ExcludeFromDescription();

        return app;
    }

    public static IEndpointRouteBuilder MapSpa(this WebApplication app)
    {
        app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/assets"))
            {
                context.Response.OnStarting(() =>
                {
                    if (context.Response.StatusCode is StatusCodes.Status200OK or StatusCodes.Status304NotModified)
                    {
                        context.Response.Headers.CacheControl = ImmutableAsset;
                    }

                    return Task.CompletedTask;
                });
            }

            return next(context);
        });

        app.MapStaticAssets();

        // Unknown server paths must stay 404s, not fall through to index.html.
        foreach (var prefix in ServerPrefixes)
        {
            app.MapFallback($"{prefix}/{{**path}}", () => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound));
        }

        // Client-side routes (/c/{slug}, /studio/{slug}, /me) load the SPA shell.
        // Without Cache-Control a browser may reuse a stale shell that names last release's chunks.
        app.MapFallbackToFile("index.html", new StaticFileOptions
        {
            OnPrepareResponse = file => file.Context.Response.Headers.CacheControl = "no-cache",
        });

        return app;
    }
}
