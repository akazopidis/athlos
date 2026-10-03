using Athlos.Tenancy;
using Microsoft.Extensions.Options;

namespace Athlos.Gateway.Tests;

// Service-to-service calls are signed like the gateway's, and cannot carry trusted headers.
public sealed class SignedRequestHandlerTests
{
    private static readonly GatewaySignature Signature =
        new(Options.Create(new GatewaySignatureOptions { Key = GatewayFactory.SigningKey }), TimeProvider.System);

    [Fact]
    public async Task Outgoing_calls_are_signed_and_carry_no_tenant()
    {
        HttpRequestMessage? sent = null;
        using var client = new HttpClient(new SignedRequestHandler(Signature) { InnerHandler = new Capture(r => sent = r) })
        {
            BaseAddress = new Uri("https://tenancy-api"),
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/internal/tenants/abc/payout-account?x=1");
        request.Headers.Add(TrustedHeaders.TenantId, "smuggled");

        await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.False(sent!.Headers.Contains(TrustedHeaders.TenantId));
        Assert.True(Signature.Verify("GET", "/internal/tenants/abc/payout-account?x=1", null, null,
            sent.Headers.GetValues(GatewaySignature.TimestampHeader).Single(),
            sent.Headers.GetValues(GatewaySignature.SignatureHeader).Single()));
    }

    private sealed class Capture(Action<HttpRequestMessage> capture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture(request);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
