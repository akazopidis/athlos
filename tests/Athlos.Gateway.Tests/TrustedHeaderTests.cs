using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace Athlos.Gateway.Tests;

// Services trust X-Tenant-Id and X-User-Role because only the gateway reaches them (design §3.2).
// This proves a caller cannot smuggle them through: the gateway strips them on every route.
public sealed class TrustedHeaderTests : IAsyncLifetime
{
    private WebApplication? _backend;
    private WebApplicationFactory<Program>? _gateway;

    public async ValueTask InitializeAsync()
    {
        // A stand-in for tenancy-api that echoes the headers it received.
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _backend = builder.Build();
        _backend.MapGet("/api/v1/plans", (HttpRequest request) =>
            request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase));
        await _backend.StartAsync(TestContext.Current.CancellationToken);

        _gateway = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            web.UseSetting("ReverseProxy:Clusters:tenancy:Destinations:tenancy-api:Address", _backend.Urls.First()));
    }

    [Fact]
    public async Task Spoofed_tenant_and_role_headers_never_reach_a_service()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/plans");
        request.Headers.Add("X-Tenant-Id", "0198a3b2-0000-7000-8000-000000000001");
        request.Headers.Add("X-User-Role", "Owner");
        request.Headers.Add("X-Unrelated", "kept");

        var response = await _gateway!.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);
        var received = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(TestContext.Current.CancellationToken);

        Assert.NotNull(received);
        Assert.DoesNotContain("X-Tenant-Id", received.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-User-Role", received.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("X-Unrelated", received.Keys, StringComparer.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        if (_gateway is not null) await _gateway.DisposeAsync();
        if (_backend is not null) await _backend.DisposeAsync();
    }
}
