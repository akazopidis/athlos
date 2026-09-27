using System.Net.Http.Headers;
using Athlos.Tenancy;
using Athlos.Tenancy.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace Athlos.Testing;

// tenancy-api against the test database, trusting TestTokens instead of Entra.
// (Anchored on TenancyDbContext: several referenced projects have a top-level Program.)
public sealed class TenancyApiFactory(TenancyDatabase database) : WebApplicationFactory<TenancyDbContext>
{
    public const string SigningKey = "test-gateway-signing-key-0123456789abcdef0123";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:tenancy-db", database.AppConnectionString);
        builder.UseSetting("GatewaySignature:Key", SigningKey);
        builder.TrustTestTokens();
    }

    // Sends a request the way the gateway forwards it: trusted headers set, then signed.
    public Task<HttpResponseMessage> SendAsGateway(
        HttpRequestMessage request, Guid? userId = null, Guid? tenantId = null, TenantRole? role = null)
    {
        if (userId is { } user)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.For(user, $"{user}@test.local", "Test User"));
        }

        if (tenantId is { } tenant) request.Headers.Add(TrustedHeaders.TenantId, tenant.ToString());
        if (role is { } r) request.Headers.Add(TrustedHeaders.UserRole, r.ToString());

        var signer = new GatewaySignature(Options.Create(new GatewaySignatureOptions { Key = SigningKey }), TimeProvider.System);
        var signed = signer.Sign(
            request.Method.Method,
            request.RequestUri!.OriginalString,
            tenantId?.ToString(),
            role?.ToString());
        request.Headers.Add(GatewaySignature.TimestampHeader, signed.Timestamp);
        request.Headers.Add(GatewaySignature.SignatureHeader, signed.Signature);

        return CreateClient().SendAsync(request);
    }
}
