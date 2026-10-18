using Athlos.Media;
using System.Text.Json.Serialization;
using Athlos.Messaging;
using Athlos.Tenancy;
using Athlos.Tenancy.Api.Data;
using Athlos.Tenancy.Api.Features.Account;
using Athlos.Tenancy.Api.Features.Admin;
using Athlos.Tenancy.Api.Features.Internal;
using Athlos.Tenancy.Api.Features.Payouts;
using Athlos.Tenancy.Api.Features.Plans;
using Athlos.Tenancy.Api.Features.Storefront;
using Athlos.Tenancy.Api.Features.Team;
using Athlos.Tenancy.Api.Features.Tenants;
using Athlos.Tenancy.Api.Features.Workspace;
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
builder.Services.AddTenantDbContext<TenancyDbContext>(options => options
    .UseNpgsql(
        builder.Configuration.GetConnectionString("tenancy-db"),
        npgsql => npgsql.ConfigureDataSource(dataSource => dataSource.UseEntraTokenIfNoPassword(builder.Configuration["AZURE_CLIENT_ID"])))
    .UseSnakeCaseNamingConvention());
builder.Services.AddStripeConnect(builder.Configuration);
// Deleting a workspace asks catalog and enrollment to erase their part (design §3.4), signed.
builder.Services.AddHttpClient(ServiceTenantErasure.Catalog, http => http.BaseAddress = new Uri("https+http://catalog-api")).AddSignedRequests();
builder.Services.AddHttpClient(ServiceTenantErasure.Enrollment, http => http.BaseAddress = new Uri("https+http://enrollment-api")).AddSignedRequests();
builder.Services.AddHttpClient(ServiceTenantErasure.Ordering, http => http.BaseAddress = new Uri("https+http://ordering-api")).AddSignedRequests();
builder.Services.AddScoped<ITenantErasure, ServiceTenantErasure>();
// Suspending a workspace asks catalog to hide its programs (design §3.4), signed.
builder.Services.AddHttpClient(ServiceTenantSuspension.Catalog, http => http.BaseAddress = new Uri("https+http://catalog-api")).AddSignedRequests();
builder.Services.AddScoped<ITenantSuspension, ServiceTenantSuspension>();
// Deleting an account asks ordering and enrollment to erase the customer's part, then Graph removes
// the Entra user (design §5.2).
builder.Services.AddHttpClient(ServiceCustomerErasure.Ordering, http => http.BaseAddress = new Uri("https+http://ordering-api")).AddSignedRequests();
builder.Services.AddHttpClient(ServiceCustomerErasure.Enrollment, http => http.BaseAddress = new Uri("https+http://enrollment-api")).AddSignedRequests();
builder.Services.AddScoped<ICustomerErasure, ServiceCustomerErasure>();
builder.Services.Configure<EntraAccountDeletionOptions>(builder.Configuration.GetSection("EntraAccountDeletion"));
builder.Services.AddHttpClient<IEntraUsers, GraphEntraUsers>();
// The staff invite email leaves through the outbox to the notifications queue (design §5.2, §5.3).
builder.AddOutbox<TenancyDbContext>();

// The coach's uploaded logo (design §8): ConnectionStrings:media is Azurite locally and the account's
// blob endpoint in Azure (managed identity), the same container as catalog-api's program covers.
// No health check: a logo is optional, so storage must not make tenancy-api unready.
builder.AddAzureBlobServiceClient("media", settings => settings.DisableHealthChecks = true);
builder.Services.AddSingleton<MediaStore>();
builder.Services.AddValidation();
builder.Services.AddOpenApi(options =>
    // Slices nest their Request/Response records; name schemas after the slice (CreateTenantRequest).
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

// Public
app.MapListPlans();
app.MapGroup("/api/v1/marketplace")
    .MapGetStorefront()
    .MapListStorefronts();

// Signed-in user, no tenant yet
app.MapCreateTenant();
app.MapAcceptInvite();
var me = app.MapGroup("/api/v1/me")
    .RequireAuthorization()
    .WithTenantTransaction<TenancyDbContext>();
me.MapListMyMemberships();
me.MapUpsertMyProfile();
// Deleting the account manages its own transactions, around the calls to other services.
app.MapDeleteMyAccount();

// Coach dashboard: tenant and role come from the gateway
var studio = app.MapGroup("/api/v1/studio/{tenantSlug}")
    .RequireAuthorization()
    .RequireTenant()
    .WithTenantTransaction<TenancyDbContext>();
studio.MapGetWorkspace();
studio.MapUpdateWorkspace();
studio.MapWorkspaceLogo();
studio.MapPayouts();
studio.MapStaffInvites();
studio.MapTeamMembers();

// Deleting the workspace manages its own transaction, after the calls to other services.
app.MapGroup("/api/v1/studio/{tenantSlug}")
    .RequireAuthorization()
    .RequireTenant()
    .MapDeleteWorkspace();

// Platform admin (Entra app role Platform.Admin): every workspace, its status and plan (design §1, §3.4)
app.MapPlatformAdmin();

// Gateway only
app.MapLookupMembership();
app.MapGetTenantPlan();
app.MapGetPayoutAccount();
// The notifications-worker (signed): where a workspace's sale email goes
app.MapGetOwnerEmails();

// Stripe, through the gateway (no user; the Stripe-Signature is the authentication): Connect account changes
app.MapStripeConnectWebhook();

app.Run();
