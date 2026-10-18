namespace Athlos.Gateway;

// The gateway serves the SPA's Vite build from wwwroot (design §8.1).
public static class Spa
{
    // /internal is service-to-service only and never routed by the gateway (design §7.1).
    private static readonly string[] ServerPrefixes = ["/api", "/webhooks", "/internal", "/health"];

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

        app.MapGet("/config.json", (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(config);
            })
            .ExcludeFromDescription();

        return app;
    }

    public static IEndpointRouteBuilder MapSpa(this WebApplication app)
    {
        app.MapStaticAssets();

        // Unknown server paths must stay 404s, not fall through to index.html.
        foreach (var prefix in ServerPrefixes)
        {
            app.MapFallback($"{prefix}/{{**path}}", () => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound));
        }

        // Client-side routes (/c/{slug}, /studio/{slug}, /me) load the SPA shell.
        app.MapFallbackToFile("index.html");

        return app;
    }
}
