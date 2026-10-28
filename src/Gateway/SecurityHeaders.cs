namespace Athlos.Gateway;

// Basic security headers from design §5.1. HSTS is added by UseHsts outside Development.
// The policy names this stage's own hosts when it knows them (ZAP baseline, design §15.1): the Entra
// External ID authority (Spa:EntraAuthority) for MSAL's token calls and silent renew, and the blob
// endpoint (Spa:ImageOrigin) that serves covers and logos as signed links. Without them (locally, or
// before Entra is set up) it falls back to any *.ciamlogin.com and any https image.
public static class SecurityHeaders
{
    // Inline styles stay allowed: Radix positions popovers and menus with style attributes.
    public static string ContentSecurityPolicy(IConfiguration configuration)
    {
        var signIn = Origin(configuration["Spa:EntraAuthority"]) ?? "https://*.ciamlogin.com";
        var images = Origin(configuration["Spa:ImageOrigin"]) ?? "https:";
        return "default-src 'self'; " +
            "script-src 'self'; " +
            "style-src 'self' 'unsafe-inline'; " +
            $"img-src 'self' data: {images}; " +
            "font-src 'self'; " +
            $"connect-src 'self' {signIn} https://login.microsoftonline.com; " +
            $"frame-src {signIn}; " +
            "frame-ancestors 'none'; " +
            "base-uri 'self'; " +
            "form-action 'self'";
    }

    // The SPA uses no camera, microphone, location or payment API (Stripe Checkout is its own page).
    private const string PermissionsPolicy = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";

    public static WebApplication UseSecurityHeaders(this WebApplication app)
    {
        var policy = ContentSecurityPolicy(app.Configuration);
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers.ContentSecurityPolicy = policy;
            headers["Permissions-Policy"] = PermissionsPolicy;
            // Other sites may link to Athlos but not load its files into their pages.
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            await next(context);
        });
        return app;
    }

    private static string? Origin(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
}
