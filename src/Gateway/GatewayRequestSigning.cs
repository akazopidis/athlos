using Athlos.Tenancy;
using Yarp.ReverseProxy.Transforms;

namespace Athlos.Gateway;

public static class GatewayRequestSigning
{
    // Adds the proof services require (see GatewaySignature) to every proxied request.
    public static ValueTask SignAsync(RequestTransformContext context)
    {
        var headers = context.ProxyRequest.Headers;
        var signature = context.HttpContext.RequestServices.GetRequiredService<GatewaySignature>();

        var signed = signature.Sign(
            context.ProxyRequest.Method.Method,
            context.Path + context.Query.QueryString,
            headers.TryGetValues(TrustedHeaders.TenantId, out var tenant) ? string.Join(',', tenant) : null,
            headers.TryGetValues(TrustedHeaders.UserRole, out var role) ? string.Join(',', role) : null);

        headers.Add(GatewaySignature.TimestampHeader, signed.Timestamp);
        headers.Add(GatewaySignature.SignatureHeader, signed.Signature);
        return ValueTask.CompletedTask;
    }
}
