using System.Net.Http.Headers;
using Athlos.Catalog.Api.Data;
using Athlos.Tenancy;
using Athlos.Tenancy.Api.Data;
using Athlos.Tenancy.Api.Features.Payouts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Athlos.Testing;

// A service against its test database, trusting TestTokens instead of Entra. TAnchor is any type in
// the service's assembly (several referenced projects have a top-level Program).
public abstract class ServiceApiFactory<TAnchor>(AthlosDatabases databases, string service, AthlosStorage? storage = null) : WebApplicationFactory<TAnchor>
    where TAnchor : class
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting($"ConnectionStrings:{service}-db", databases.AppConnectionString(service));
        builder.UseSetting("GatewaySignature:Key", TestGateway.SigningKey);
        if (storage is not null)
        {
            builder.UseSetting("ConnectionStrings:media", storage.ConnectionString);
        }

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

public sealed class TenancyApiFactory(AthlosDatabases databases, FakeStripeConnect? stripe = null)
    : ServiceApiFactory<TenancyDbContext>(databases, "tenancy")
{
    public const string PublicUrl = "https://athlos.test";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("App:PublicUrl", PublicUrl);

        // Without a fake, Stripe counts as not configured, as in an environment without a key.
        if (stripe is not null)
        {
            builder.ConfigureTestServices(services => services.AddSingleton<IStripeConnect>(stripe));
        }
    }
}

public sealed class CatalogApiFactory(AthlosDatabases databases, AthlosStorage storage)
    : ServiceApiFactory<CatalogDbContext>(databases, "catalog", storage)
{
    public FakePayoutReadiness Payouts { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.AddSingleton<Athlos.Catalog.Api.Features.Programs.IPayoutReadiness>(Payouts));
    }
}

// The signing key the services under test trust, standing in for the gateway's.
public static class TestGateway
{
    public const string SigningKey = "test-gateway-signing-key-0123456789abcdef0123";

    public static GatewaySignature Signer(TimeProvider time) =>
        new(Options.Create(new GatewaySignatureOptions { Key = SigningKey }), time);
}
