using System.Net;
using Athlos.Tenancy;
using Athlos.Testing;
using Microsoft.Extensions.Options;

namespace Athlos.IsolationTests;

// Layer 1 during a signing-key rotation (design §3.2, infra/README.md): a service accepts requests
// signed with its key and, only while one is set, with the second key it also accepts. Nothing else.
public sealed class SigningKeyRotationTests(AthlosDatabases database)
{
    private static readonly string NextKey = new('n', 48);

    [Fact]
    public void A_signature_verifies_with_the_key_or_the_one_also_accepted()
    {
        var current = Signer(TestGateway.SigningKey);
        var next = Signer(NextKey);
        var during = new GatewaySignature(Options.Create(new GatewaySignatureOptions { Key = TestGateway.SigningKey, AlsoAcceptKey = NextKey }), TimeProvider.System);

        var fromCurrent = current.Sign("GET", "/api/v1/plans", null, null);
        var fromNext = next.Sign("GET", "/api/v1/plans", null, null);
        var fromOther = Signer(new string('x', 48)).Sign("GET", "/api/v1/plans", null, null);

        Assert.True(during.Verify("GET", "/api/v1/plans", null, null, fromCurrent.Timestamp, fromCurrent.Signature));
        Assert.True(during.Verify("GET", "/api/v1/plans", null, null, fromNext.Timestamp, fromNext.Signature));
        Assert.False(during.Verify("GET", "/api/v1/plans", null, null, fromOther.Timestamp, fromOther.Signature));
        Assert.False(current.Verify("GET", "/api/v1/plans", null, null, fromNext.Timestamp, fromNext.Signature));
    }

    // Through a running service: the next key is accepted only once the service is configured with it.
    [Fact]
    public async Task A_service_accepts_the_next_key_only_while_it_is_configured()
    {
        await using var rotating = new TenancyApiFactory(database);
        rotating.Settings["GatewaySignature:AlsoAcceptKey"] = NextKey;
        await using var plain = new TenancyApiFactory(database);

        Assert.Equal(HttpStatusCode.OK, await SendSignedWithNextKey(rotating));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendSignedWithNextKey(plain));
    }

    [Fact]
    public async Task A_second_key_shorter_than_the_minimum_stops_the_service()
    {
        await using var weak = new TenancyApiFactory(database);
        weak.Settings["GatewaySignature:AlsoAcceptKey"] = "short";

        Assert.ThrowsAny<Exception>(() => weak.CreateClient());
        await Task.CompletedTask;
    }

    private static async Task<HttpStatusCode> SendSignedWithNextKey(TenancyApiFactory api)
    {
        var signed = Signer(NextKey).Sign("GET", "/api/v1/plans", null, null);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/plans");
        request.Headers.Add(GatewaySignature.TimestampHeader, signed.Timestamp);
        request.Headers.Add(GatewaySignature.SignatureHeader, signed.Signature);
        return (await api.CreateClient().SendAsync(request, TestContext.Current.CancellationToken)).StatusCode;
    }

    private static GatewaySignature Signer(string key) =>
        new(Options.Create(new GatewaySignatureOptions { Key = key }), TimeProvider.System);
}
