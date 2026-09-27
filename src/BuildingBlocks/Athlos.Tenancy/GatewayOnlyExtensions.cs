using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Athlos.Tenancy;

public static class GatewayOnlyExtensions
{
    // Registers the signer/verifier. The key is required: a service must not start without it.
    public static IServiceCollection AddGatewaySignature(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<GatewaySignatureOptions>()
            .Bind(configuration.GetSection(GatewaySignatureOptions.Section))
            .Validate(GatewaySignatureOptions.IsValid, $"{GatewaySignatureOptions.Section}:Key must be at least {GatewaySignatureOptions.MinKeyLength} characters.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<GatewaySignature>();
        return services;
    }

    // Services only: every request except health probes and /version must carry a valid gateway signature.
    public static IApplicationBuilder UseGatewayOnly(this WebApplication app)
    {
        var development = app.Environment.IsDevelopment();

        return app.Use(async (context, next) =>
        {
            if (IsExempt(context.Request.Path, development) || HasValidSignature(context))
            {
                await next(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = { Title = "Requests must come through the gateway." },
            });
        });
    }

    private static bool IsExempt(PathString path, bool development) =>
        path.StartsWithSegments("/health")
        || path == "/version"
        // API documents are only mapped in Development.
        || (development && (path.StartsWithSegments("/openapi") || path.StartsWithSegments("/scalar")));

    private static bool HasValidSignature(HttpContext context)
    {
        var request = context.Request;
        var signature = context.RequestServices.GetRequiredService<GatewaySignature>();

        return signature.Verify(
            request.Method,
            request.Path + request.QueryString,
            request.Headers[TrustedHeaders.TenantId],
            request.Headers[TrustedHeaders.UserRole],
            request.Headers[GatewaySignature.TimestampHeader],
            request.Headers[GatewaySignature.SignatureHeader]);
    }
}
