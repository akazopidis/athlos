using System.Net;
using System.Net.Http.Headers;
using Athlos.Tenancy;
using Athlos.Testing;
using Microsoft.Extensions.Options;

namespace Athlos.IsolationTests;

// Layer 1 (design §3.2): a service only acts on requests the gateway signed. These tests call a
// service directly, as someone who found its address would, and expect every attempt to fail.
public sealed class BypassGatewayTests(AthlosDatabases database) : IAsyncLifetime
{
    private const string TenantA = "0198a3b2-0000-7000-8000-00000000000a";
    private const string TenantB = "0198a3b2-0000-7000-8000-00000000000b";

    private readonly TenancyApiFactory _api = new(database);

    // Every tenancy-api endpoint. Add each new endpoint here.
    public static TheoryData<string, string> Endpoints => new()
    {
        { "GET", "/api/v1/plans" },
        { "GET", "/api/v1/marketplace/coaches/some-coach" },
        { "POST", "/api/v1/tenants" },
        { "GET", "/api/v1/me/memberships" },
        { "PUT", "/api/v1/me/profile" },
        { "DELETE", "/api/v1/me" },
        { "GET", "/api/v1/studio/some-coach/workspace" },
        { "PUT", "/api/v1/studio/some-coach/workspace" },
        { "DELETE", "/api/v1/studio/some-coach/workspace" },
        { "GET", $"/internal/tenants/some-coach/members/{TenantB}" },
        { "GET", "/internal/tenants/some-coach/plan" },
        { "POST", "/api/v1/studio/some-coach/payouts/onboarding" },
        { "GET", "/api/v1/studio/some-coach/payouts" },
        { "GET", $"/internal/tenants/{TenantA}/payout-account" },
        { "GET", $"/internal/tenants/{TenantA}/owner-emails" },
        { "GET", "/api/v1/studio/some-coach/invites" },
        { "POST", "/api/v1/studio/some-coach/invites" },
        { "DELETE", $"/api/v1/studio/some-coach/invites/{TenantA}" },
        { "POST", "/api/v1/invites/some-coach/accept" },
        { "GET", "/api/v1/admin/tenants" },
        { "PUT", $"/api/v1/admin/tenants/{TenantA}/status" },
        { "PUT", $"/api/v1/admin/tenants/{TenantA}/plan" },
        { "GET", "/api/v1/admin/metrics" },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Unsigned_requests_are_rejected(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(TrustedHeaders.TenantId, TenantA);
        request.Headers.Add(TrustedHeaders.UserRole, "Owner");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.For(Guid.CreateVersion7()));

        var response = await Send(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Correctly_signed_requests_are_accepted(string method, string path)
    {
        var response = await _api.SendAsGateway(
            new HttpRequestMessage(new HttpMethod(method), path), Guid.CreateVersion7(), Guid.Parse(TenantA), TenantRole.Owner);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Changing_the_tenant_after_signing_is_rejected()
    {
        using var request = Signed(Signer(TimeProvider.System), "GET", "/api/v1/plans", TenantA, "Owner");
        request.Headers.Remove(TrustedHeaders.TenantId);
        request.Headers.Add(TrustedHeaders.TenantId, TenantB);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(request)).StatusCode);
    }

    [Fact]
    public async Task Raising_the_role_after_signing_is_rejected()
    {
        using var request = Signed(Signer(TimeProvider.System), "GET", "/api/v1/plans", TenantA, "Staff");
        request.Headers.Remove(TrustedHeaders.UserRole);
        request.Headers.Add(TrustedHeaders.UserRole, "Owner");

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(request)).StatusCode);
    }

    [Fact]
    public async Task A_signature_for_another_path_is_rejected()
    {
        var signed = Signer(TimeProvider.System).Sign("GET", "/api/v1/something-else", TenantA, "Owner");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/plans");
        AddHeaders(request, signed, TenantA, "Owner");

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(request)).StatusCode);
    }

    [Fact]
    public async Task A_replayed_signature_older_than_the_window_is_rejected()
    {
        var twoMinutesAgo = new FixedTimeProvider(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2));

        var response = await Send(Signed(Signer(twoMinutesAgo), "GET", "/api/v1/plans", TenantA, "Owner"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_signature_made_with_another_key_is_rejected()
    {
        var wrongKey = new GatewaySignature(
            Options.Create(new GatewaySignatureOptions { Key = new string('x', 48) }), TimeProvider.System);

        var response = await Send(Signed(wrongKey, "GET", "/api/v1/plans", TenantA, "Owner"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/version")]
    public async Task Probes_and_version_work_without_a_signature(string path)
    {
        var response = await _api.CreateClient().GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private Task<HttpResponseMessage> Send(HttpRequestMessage request) =>
        _api.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);

    private static GatewaySignature Signer(TimeProvider time) => TestGateway.Signer(time);

    private static HttpRequestMessage Signed(GatewaySignature signer, string method, string path, string tenantId, string role)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        AddHeaders(request, signer.Sign(method, path, tenantId, role), tenantId, role);
        return request;
    }

    private static void AddHeaders(HttpRequestMessage request, GatewaySignature.SignedHeaders signed, string tenantId, string role)
    {
        request.Headers.Add(TrustedHeaders.TenantId, tenantId);
        request.Headers.Add(TrustedHeaders.UserRole, role);
        request.Headers.Add(GatewaySignature.TimestampHeader, signed.Timestamp);
        request.Headers.Add(GatewaySignature.SignatureHeader, signed.Signature);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
