using System.Text.Json.Serialization;
using Athlos.Messaging;
using Athlos.Ordering.Api.Data;
using Athlos.Ordering.Api.Features.Checkout;
using Athlos.Ordering.Api.Features.Customers;
using Athlos.Ordering.Api.Features.Orders;
using Athlos.Ordering.Api.Features.Payouts;
using Athlos.Ordering.Api.Features.Refunds;
using Athlos.Ordering.Api.Features.Tenants;
using Athlos.Ordering.Api.Features.Webhooks;
using Athlos.Tenancy;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddGatewaySignature(builder.Configuration);
builder.Services.AddAthlosAuthentication(builder.Configuration);
builder.Services.AddPlatformAdmin();
builder.Services.AddTenantContext();
builder.Services.AddTenantDbContext<OrderingDbContext>(options => options
    .UseNpgsql(
        builder.Configuration.GetConnectionString("ordering-db"),
        npgsql => npgsql.ConfigureDataSource(dataSource => dataSource.UseEntraTokenIfNoPassword(builder.Configuration["AZURE_CLIENT_ID"])))
    .UseSnakeCaseNamingConvention());

// OrderPaid leaves through the outbox to Service Bus (design §2.2).
builder.AddOutbox<OrderingDbContext>();

// Stripe Checkout and webhook verification; checkout asks catalog and tenancy with signed calls.
builder.Services.AddPayments(builder.Configuration);
// Coach shares whose transfer failed after payment are tried again (design §2.2).
builder.Services.AddHostedService<PayoutRetries>();
builder.Services.AddHttpClient(SellerDirectory.Catalog, http => http.BaseAddress = new Uri("https+http://catalog-api")).AddSignedRequests();
builder.Services.AddHttpClient(SellerDirectory.Tenancy, http => http.BaseAddress = new Uri("https+http://tenancy-api")).AddSignedRequests();
builder.Services.AddSingleton<ISellerDirectory, SellerDirectory>();

builder.Services.AddValidation();
builder.Services.AddOpenApi(options =>
    // Slices nest their Request/Response records; name schemas after the slice (StartCheckoutRequest).
    options.CreateSchemaReferenceId = type => type.Type.IsNested
        ? type.Type.DeclaringType!.Name + type.Type.Name
        : OpenApiOptions.CreateDefaultSchemaReferenceId(type));
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseGatewayOnly();
app.UseAuthentication();
app.UseAuthorization();
app.UseTenantContext();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapDefaultEndpoints();

// Customer: buy (enters the coach's tenant itself) and list their own orders
app.MapStartCheckout();
app.MapGroup("/api/v1/me")
    .RequireAuthorization()
    .WithTenantTransaction<OrderingDbContext>()
    .MapListMyOrders();

// Coach dashboard: tenant and role come from the gateway
app.MapGroup("/api/v1/studio/{tenantSlug}")
    .RequireAuthorization()
    .RequireTenant()
    .WithTenantTransaction<OrderingDbContext>()
    .MapListSales();

// The coach refunds a sale: Stripe calls first, then short transactions inside the tenant (OrderRefunds)
app.MapGroup("/api/v1/studio/{tenantSlug}")
    .RequireAuthorization()
    .RequireTenant()
    .MapRefundSale();

// Platform admin (Entra app role Platform.Admin): coach shares waiting for a transfer
app.MapAdminPayouts();

// Stripe, through the gateway (no user; the Stripe-Signature is the authentication)
app.MapStripeWebhook();

// Tenancy-api only (signed): anonymize a deleted account's orders (design §5.2)
app.MapEraseCustomer();
// Tenancy-api only (signed): before a workspace is erased, no refundable sales or open checkouts (design §3.4)
app.MapPrepareTenantDeletion();

app.Run();
