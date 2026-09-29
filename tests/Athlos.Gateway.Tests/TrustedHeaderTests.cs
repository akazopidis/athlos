using System.Net.Http.Json;
using Athlos.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Athlos.Gateway.Tests;

// Services trust X-Tenant-Id and X-User-Role because only the gateway sets them (design §3.2).
// These tests prove a caller cannot smuggle them through, and that what the gateway forwards is signed.
public sealed class TrustedHeaderTests : IAsyncLifetime
{
    private WebApplication? _backend;
    private WebApplicationFactory<MembershipLookup>? _gateway;

    public async ValueTask InitializeAsync()
    {
        // A stand-in for tenancy-api that echoes the headers it received.
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _backend = builder.Build();
        _backend.MapGet("/api/v1/plans", (HttpRequest request) =>
            request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase));
        await _backend.StartAsync(TestContext.Current.CancellationToken);

        _gateway = new GatewayFactory().WithWebHostBuilder(web =>
            web.UseSetting("ReverseProxy:Clusters:tenancy:Destinations:tenancy-api:Address", _backend.Urls.First()));
    }

    [Fact]
    public async Task Spoofed_tenant_and_role_headers_never_reach_a_service()
    {
        var received = await SendThroughGateway(request =>
        {
            request.Headers.Add(TrustedHeaders.TenantId, "0198a3b2-0000-7000-8000-000000000001");
            request.Headers.Add(TrustedHeaders.UserRole, "Owner");
            request.Headers.Add("X-Unrelated", "kept");
        });

        Assert.DoesNotContain(TrustedHeaders.TenantId, received.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(TrustedHeaders.UserRole, received.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("X-Unrelated", received.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Forwarded_requests_carry_a_valid_gateway_signature()
    {
        var received = await SendThroughGateway(_ => { });

        Assert.True(Verifier().Verify(
            "GET", "/api/v1/plans", tenantId: null, userRole: null,
            received[GatewaySignature.TimestampHeader], received[GatewaySignature.SignatureHeader]));
    }

    [Fact]
    public async Task A_signature_sent_by_the_caller_is_replaced()
    {
        var received = await SendThroughGateway(request =>
        {
            request.Headers.Add(GatewaySignature.TimestampHeader, "1");
            request.Headers.Add(GatewaySignature.SignatureHeader, "forged");
        });

        Assert.NotEqual("forged", received[GatewaySignature.SignatureHeader]);
        Assert.NotEqual("1", received[GatewaySignature.TimestampHeader]);
    }

    private async Task<Dictionary<string, string>> SendThroughGateway(Action<HttpRequestMessage> configure)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/plans");
        configure(request);

        var response = await _gateway!.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var received = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(TestContext.Current.CancellationToken);
        Assert.NotNull(received);
        return new Dictionary<string, string>(received, StringComparer.OrdinalIgnoreCase);
    }

    private static GatewaySignature Verifier() =>
        new(Options.Create(new GatewaySignatureOptions { Key = GatewayFactory.SigningKey }), TimeProvider.System);

    public async ValueTask DisposeAsync()
    {
        if (_gateway is not null) await _gateway.DisposeAsync();
        if (_backend is not null) await _backend.DisposeAsync();
    }
}
