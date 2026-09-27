using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Athlos.Tenancy;
using Athlos.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Athlos.Gateway.Tests;

// Layer 1 for studio routes (design §3.2): the gateway resolves the workspace and the caller's
// membership, answers 404 for non-members, and forwards the tenant and role in signed headers.
public sealed class StudioAccessTests : IAsyncLifetime
{
    private static readonly Guid TenantA = Guid.CreateVersion7();
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid Stranger = Guid.CreateVersion7();

    private WebApplication? _tenancy;
    private WebApplicationFactory<MembershipLookup>? _gateway;
    private int _lookups;
    private int _studioCalls;

    public async ValueTask InitializeAsync()
    {
        // A stand-in for tenancy-api: the internal lookup (which must be signed) and one studio endpoint.
        var verifier = new GatewaySignature(
            Options.Create(new GatewaySignatureOptions { Key = GatewayFactory.SigningKey }), TimeProvider.System);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _tenancy = builder.Build();

        _tenancy.MapGet("/internal/tenants/{slug}/members/{userId:guid}", (string slug, Guid userId, HttpRequest request) =>
        {
            Interlocked.Increment(ref _lookups);
            if (!verifier.Verify("GET", request.Path, null, null,
                    request.Headers[GatewaySignature.TimestampHeader], request.Headers[GatewaySignature.SignatureHeader]))
            {
                return Results.Unauthorized();
            }

            return slug == "coach-a" && userId == Owner
                ? Results.Ok(new { tenantId = TenantA, role = "Owner", plan = "Free" })
                : Results.NotFound();
        });

        _tenancy.MapGet("/api/v1/studio/{slug}/workspace", (HttpRequest request) =>
        {
            Interlocked.Increment(ref _studioCalls);
            return request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        });

        await _tenancy.StartAsync(TestContext.Current.CancellationToken);

        _gateway = new GatewayFactory().WithWebHostBuilder(web =>
            web.UseSetting("ReverseProxy:Clusters:tenancy:Destinations:tenancy-api:Address", _tenancy.Urls.First())
               .UseSetting("services:tenancy-api:http:0", _tenancy.Urls.First()));
    }

    [Fact]
    public async Task Studio_requests_need_a_signed_in_user()
    {
        var response = await Studio("coach-a", userId: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, _studioCalls);
    }

    [Fact]
    public async Task A_non_member_gets_404_and_the_service_is_never_called()
    {
        var response = await Studio("coach-a", Stranger);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, _studioCalls);
    }

    [Theory]
    [InlineData("no-such-coach")]
    [InlineData("Not_A_Slug")]
    public async Task An_unknown_or_malformed_workspace_is_404(string slug)
    {
        var response = await Studio(slug, Owner);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_member_is_forwarded_with_the_resolved_tenant_and_role()
    {
        var received = await ForwardedHeaders(await Studio("coach-a", Owner));

        Assert.Equal(TenantA.ToString(), received[TrustedHeaders.TenantId]);
        Assert.Equal("Owner", received[TrustedHeaders.UserRole]);
        var verifier = new GatewaySignature(Options.Create(new GatewaySignatureOptions { Key = GatewayFactory.SigningKey }), TimeProvider.System);
        Assert.True(verifier.Verify("GET", "/api/v1/studio/coach-a/workspace", TenantA.ToString(), "Owner",
            received[GatewaySignature.TimestampHeader], received[GatewaySignature.SignatureHeader]));
    }

    [Fact]
    public async Task A_member_cannot_point_the_request_at_another_tenant()
    {
        var response = await Studio("coach-a", Owner, spoofTenant: Guid.CreateVersion7().ToString());

        var received = await ForwardedHeaders(response);
        Assert.Equal(TenantA.ToString(), received[TrustedHeaders.TenantId]);
    }

    [Fact]
    public async Task Memberships_are_cached()
    {
        await Studio("coach-a", Owner);
        await Studio("coach-a", Owner);
        await Studio("coach-a", Owner);

        Assert.Equal(1, _lookups);
    }

    private async Task<HttpResponseMessage> Studio(string slug, Guid? userId, string? spoofTenant = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/studio/{slug}/workspace");
        if (userId is { } user)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.For(user));
        }

        if (spoofTenant is not null)
        {
            request.Headers.Add(TrustedHeaders.TenantId, spoofTenant);
        }

        return await _gateway!.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<Dictionary<string, string>> ForwardedHeaders(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var headers = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(TestContext.Current.CancellationToken);
        return new Dictionary<string, string>(headers!, StringComparer.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        if (_gateway is not null) await _gateway.DisposeAsync();
        if (_tenancy is not null) await _tenancy.DisposeAsync();
    }
}
