using System.Net.Http.Headers;
using Athlos.Catalog.Api.Data;
using Athlos.Tenancy;
using Athlos.Tenancy.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace Athlos.Testing;

// A service against its test database, trusting TestTokens instead of Entra. TAnchor is any type in
// the service's assembly (several referenced projects have a top-level Program).
public abstract class ServiceApiFactory<TAnchor>(AthlosDatabases databases, string service) : WebApplicationFactory<TAnchor>
    where TAnchor : class
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting($"ConnectionStrings:{service}-db", databases.AppConnectionString(service));
        builder.UseSetting("GatewaySignature:Key", TestGateway.SigningKey);
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

        var signed = TestGateway.Signer(TimeProvider.System)
            .Sign(request.Method.Method, request.RequestUri!.OriginalString, tenantId?.ToString(), role?.ToString());
        request.Headers.Add(GatewaySignature.TimestampHeader, signed.Timestamp);
        request.Headers.Add(GatewaySignature.SignatureHeader, signed.Signature);

        return CreateClient().SendAsync(request);
    }
}

public sealed class TenancyApiFactory(AthlosDatabases databases) : ServiceApiFactory<TenancyDbContext>(databases, "tenancy");

public sealed class CatalogApiFactory(AthlosDatabases databases) : ServiceApiFactory<CatalogDbContext>(databases, "catalog");

// The signing key the services under test trust, standing in for the gateway's.
public static class TestGateway
{
    public const string SigningKey = "test-gateway-signing-key-0123456789abcdef0123";

    public static GatewaySignature Signer(TimeProvider time) =>
        new(Options.Create(new GatewaySignatureOptions { Key = SigningKey }), time);
}
