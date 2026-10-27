using System.Text.Json.Serialization;
using Athlos.Messaging;
using Athlos.Ordering.Api.Data;
using Athlos.Ordering.Api.Features.Cart;
using Athlos.Ordering.Api.Features.Checkout;
using Athlos.Ordering.Api.Features.Customers;
using Athlos.Ordering.Api.Features.Discounts;
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
// The discount code a workspace shows in the shop: catalog-api keeps the copy (design §2.4).
builder.Services.AddSingleton<IPublicDiscounts, CatalogPublicDiscounts>();

builder.Services.AddValidation();
builder.Services.AddOpenApi(options =>
{
    // Slices nest their Request/Response records; name schemas after the slice (StartCheckoutRequest).
    options.CreateSchemaReferenceId = type => type.Type.IsNested
        ? type.Type.DeclaringType!.Name + type.Type.Name
        : OpenApiOptions.CreateDefaultSchemaReferenceId(type);
    // The discount codes' handlers read the tenant from ITenantContext, not from {tenantSlug}.
    options.AddOperationTransformer<Athlos.Ordering.Api.RouteParameterTransformer>();
});
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
// Customer: price the cart with a discount code (enters each coach's tenant itself)
app.MapCheckDiscount();
app.MapGroup("/api/v1/me")
    .RequireAuthorization()
    .WithTenantTransaction<OrderingDbContext>()
    .MapListMyOrders();
app.MapGroup("/api/v1/me")
    .RequireAuthorization()
    .WithTenantTransaction<OrderingDbContext>()
    .MapGetMyReceipt();
// Customer: ask for a refund within the window (reads their own order, then enters its tenant itself)
app.MapGroup("/api/v1/me")
    .RequireAuthorization()
    .MapRequestRefund();
// Customer: their cart, kept with the account (their own row; RLS customer_id = app.user_id)
app.MapGroup("/api/v1/me")
    .RequireAuthorization()
    .WithTenantTransaction<OrderingDbContext>()
    .MapMyCart();

// Coach dashboard: tenant and role come from the gateway
app.MapGroup("/api/v1/studio/{tenantSlug}")
    .RequireAuthorization()
    .RequireTenant()
    .WithTenantTransaction<OrderingDbContext>()
    .MapListSales();
app.MapGroup("/api/v1/studio/{tenantSlug}")
    .RequireAuthorization()
    .RequireTenant()
    .WithTenantTransaction<OrderingDbContext>()
    .MapSalesSummary();
// The workspace's discount codes (design §2.4)
app.MapGroup("/api/v1/studio/{tenantSlug}")
    .RequireAuthorization()
    .RequireTenant()
    .WithTenantTransaction<OrderingDbContext>()
    .MapStudioDiscounts();

// The coach refunds a sale: Stripe calls first, then short transactions inside the tenant (OrderRefunds)
app.MapGroup("/api/v1/studio/{tenantSlug}")
    .RequireAuthorization()
    .RequireTenant()
    .MapRefundSale();

// Platform admin (Entra app role Platform.Admin): coach shares waiting for a transfer
app.MapAdminPayouts();
// Platform admin: refund requests a coach left open for three working days (design §2.2)
app.MapAdminRefundRequests();

// Stripe, through the gateway (no user; the Stripe-Signature is the authentication)
app.MapStripeWebhook();

// Tenancy-api only (signed): anonymize a deleted account's orders (design §5.2)
app.MapEraseCustomer();
// Tenancy-api only (signed): a customer's orders for their data export (design §5.4)
app.MapExportCustomer();
// Tenancy-api only (signed): before a workspace is erased, no refundable sales or open checkouts (design §3.4)
app.MapPrepareTenantDeletion();
// Tenancy-api only (signed): one workspace's sales and discount codes for its owner's export (design §5.4)
app.MapExportTenant();

app.Run();
