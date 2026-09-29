using Microsoft.Extensions.DependencyInjection;

namespace Athlos.Tenancy;

// Service-to-service calls (for example catalog-api asking tenancy-api about a workspace) are
// signed exactly like the gateway's forwarded requests, because the receiving service accepts
// nothing else (UseGatewayOnly). They carry no tenant or role headers; the callee decides what to
// return for the ids in the path.
public sealed class SignedRequestHandler(GatewaySignature signature) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        foreach (var header in TrustedHeaders.All)
        {
            request.Headers.Remove(header);
        }

        var signed = signature.Sign(request.Method.Method, request.RequestUri!.PathAndQuery, tenantId: null, userRole: null);
        request.Headers.Add(GatewaySignature.TimestampHeader, signed.Timestamp);
        request.Headers.Add(GatewaySignature.SignatureHeader, signed.Signature);
        return base.SendAsync(request, cancellationToken);
    }
}

public static class ServiceToServiceSigning
{
    public static IHttpClientBuilder AddSignedRequests(this IHttpClientBuilder builder)
    {
        builder.Services.AddTransient<SignedRequestHandler>();
        return builder.AddHttpMessageHandler<SignedRequestHandler>();
    }
}
