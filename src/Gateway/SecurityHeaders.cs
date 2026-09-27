namespace Athlos.Gateway;

// Basic security headers from design §5.1. HSTS is added by UseHsts outside Development.
public static class SecurityHeaders
{
    // Entra External ID sign-in runs on *.ciamlogin.com; MSAL needs it for token calls and silent renew.
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: https:; " +
        "font-src 'self'; " +
        "connect-src 'self' https://*.ciamlogin.com https://login.microsoftonline.com; " +
        "frame-src https://*.ciamlogin.com; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            await next(context);
        });
}
