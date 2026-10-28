using System.Net.Http.Headers;
using Athlos.Catalog.Api.Data;
using Athlos.Messaging;
using Athlos.Ordering.Api.Data;
using Athlos.Ordering.Api.Features.Checkout;
using Athlos.Ordering.Api.Features.Discounts;
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
    // Azurite, when the test uses media; null otherwise.
    protected AthlosStorage? Storage => storage;

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
        HttpRequestMessage request, Guid? userId = null, Guid? tenantId = null, TenantRole? role = null, IReadOnlyList<string>? appRoles = null)
    {
        if (userId is { } user)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.For(user, $"{user}@test.local", "Test User", appRoles));
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

// storage: Azurite, for tests that upload a logo; without it the media client points at the emulator's
// default address and is never called.
public sealed class TenancyApiFactory(AthlosDatabases databases, FakeStripeConnect? stripe = null, AthlosStorage? storage = null)
    : ServiceApiFactory<TenancyDbContext>(databases, "tenancy", storage)
{
    public const string PublicUrl = "https://athlos.test";

    // The invite emails tenancy-api queues (design §5.2), as the outbox publisher would send them.
    public CapturingMessageSender Messages { get; } = new();

    // What deleting a workspace asks catalog-api and enrollment-api to erase (design §3.4).
    public FakeTenantErasure Erasure { get; } = new();

    // What deleting an account asks the other services and Microsoft Graph to do (design §5.2).
    public FakeCustomerErasure CustomerErasure { get; } = new();

    // The other services' parts of a data export (design §5.4).
    public FakeCustomerExport CustomerExport { get; } = new();

    // The other services' parts of a workspace export (design §5.4).
    public FakeTenantExport TenantExport { get; } = new();

    public FakeEntraUsers EntraUsers { get; } = new();

    // Extra configuration, set before the first request (for example a newer Legal:CoachTermsVersion).
    public Dictionary<string, string> Settings { get; } = [];

    // What suspending a workspace asks catalog-api to do (design §3.4).
    public FakeTenantSuspension Suspension { get; } = new();

    public FakeCatalogCoachNames CoachNames { get; } = new();

    // What reporting a program and the admin's decisions ask catalog-api (design §2.4).
    public FakeReportedPrograms ReportedPrograms { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("App:PublicUrl", PublicUrl);
        builder.UseSetting("Stripe:ConnectWebhookSecret", StripeEvents.SigningSecret);
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }
        if (Storage is null)
        {
            builder.UseSetting("ConnectionStrings:media", "UseDevelopmentStorage=true");
        }

        // The client is never used: tests replace the sender. Aspire only needs a well-formed value.
        builder.UseSetting("ConnectionStrings:messaging", "Endpoint=sb://localhost;SharedAccessKeyName=test;SharedAccessKey=dGVzdA==");
        builder.ConfigureTestServices(services =>
        {
            // Tests run the reminders' pass themselves (PayoutReminders.RemindDueAsync). Left on, every
            // test app's timer makes a pass when it starts and can take another test's due reminder.
            services.Remove(services.Single(d => d.ImplementationType == typeof(PayoutReminders)));
            // The same for the coach name copies (CoachNameCopies.CopyAsync).
            services.Remove(services.Single(d => d.ImplementationType == typeof(Athlos.Tenancy.Api.Features.Storefront.CoachNameCopies)));
            services.AddSingleton<Athlos.Tenancy.Api.Features.Storefront.ICatalogCoachNames>(CoachNames);
            services.AddSingleton<IMessageSender>(Messages);
            services.AddSingleton<Athlos.Tenancy.Api.Features.Workspace.ITenantErasure>(Erasure);
            services.AddSingleton<Athlos.Tenancy.Api.Features.Account.ICustomerErasure>(CustomerErasure);
            services.AddSingleton<Athlos.Tenancy.Api.Features.Account.ICustomerExport>(CustomerExport);
            services.AddSingleton<Athlos.Tenancy.Api.Features.Workspace.ITenantExport>(TenantExport);
            services.AddSingleton<Athlos.Tenancy.Api.Features.Account.IEntraUsers>(EntraUsers);
            services.AddSingleton<Athlos.Tenancy.Api.Features.Admin.ITenantSuspension>(Suspension);
            services.AddSingleton<Athlos.Tenancy.Api.Features.Reports.IReportedPrograms>(ReportedPrograms);
        });

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

    public FakeProgramQuota Quota { get; } = new();

    // The marketplace output cache (design §4), off unless a test sets it.
    public int CacheSeconds { get; init; }

    // Workouts added to programs that have been on sale, as enrollment-api would hear of them (design §5.3).
    public FakeProgramUpdates Updates { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // Tests read a program right after changing it; CatalogCacheTests turns the cache back on.
        builder.UseSetting("Marketplace:CacheSeconds", CacheSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<Athlos.Catalog.Api.Features.Programs.IPayoutReadiness>(Payouts);
            services.AddSingleton<Athlos.Catalog.Api.Features.Programs.IProgramQuota>(Quota);
            services.AddSingleton<Athlos.Catalog.Api.Features.Workouts.IProgramUpdates>(Updates);
        });
    }
}

// The signing key the services under test trust, standing in for the gateway's.
public static class TestGateway
{
    public const string SigningKey = "test-gateway-signing-key-0123456789abcdef0123";

    public static GatewaySignature Signer(TimeProvider time) =>
        new(Options.Create(new GatewaySignatureOptions { Key = SigningKey }), time);
}

public sealed class OrderingApiFactory(AthlosDatabases databases) : ServiceApiFactory<OrderingDbContext>(databases, "ordering")
{
    public FakeSellerDirectory Sellers { get; } = new();

    public FakeCheckoutSessions CheckoutSessions { get; } = new();

    public CapturingMessageSender Messages { get; } = new();

    public FakePublicDiscounts PublicDiscounts { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("App:PublicUrl", TenancyApiFactory.PublicUrl);
        builder.UseSetting("Stripe:WebhookSecret", StripeEvents.SigningSecret);
        // The client is never used: tests replace the sender. Aspire only needs a well-formed value.
        builder.UseSetting("ConnectionStrings:messaging", "Endpoint=sb://localhost;SharedAccessKeyName=test;SharedAccessKey=dGVzdA==");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ISellerDirectory>(Sellers);
            services.AddSingleton<ICheckoutSessions>(CheckoutSessions);
            services.AddSingleton<IMessageSender>(Messages);
            services.AddSingleton<IPublicDiscounts>(PublicDiscounts);
        });
    }

    // Sends a Stripe webhook the way Stripe (through the gateway) would.
    public Task<HttpResponseMessage> SendWebhook(string payload, string? signature = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe") { Content = new StringContent(payload) };
        request.Headers.Add("Stripe-Signature", signature ?? StripeEvents.Signature(payload));
        return SendAsGateway(request);
    }
}
