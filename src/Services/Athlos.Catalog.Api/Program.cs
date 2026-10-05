using System.Text.Json.Serialization;
using Athlos.Catalog.Api;
using Athlos.Catalog.Api.Data;
using Athlos.Catalog.Api.Features.Internal;
using Athlos.Catalog.Api.Features.Marketplace;
using Athlos.Catalog.Api.Features.Programs;
using Athlos.Catalog.Api.Features.Workouts;
using Athlos.Catalog.Api.Media;
using Athlos.Tenancy;
using Athlos.Tenancy.Data;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddGatewaySignature(builder.Configuration);
builder.Services.AddAthlosAuthentication(builder.Configuration);
builder.Services.AddTenantContext();
builder.Services.AddTenantDbContext<CatalogDbContext>(options => options
    .UseNpgsql(
        builder.Configuration.GetConnectionString("catalog-db"),
        npgsql => npgsql.ConfigureDataSource(dataSource => dataSource.UseEntraTokenIfNoPassword(builder.Configuration["AZURE_CLIENT_ID"])))
    .UseSnakeCaseNamingConvention());
// Program images (design §3.2): ConnectionStrings:media is Azurite locally and the account's blob
// endpoint in Azure, where the client signs in with the app's managed identity.
builder.AddAzureBlobServiceClient("media");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<MediaStore>();
// Service-to-service: tenancy-api tells catalog whether a workspace may sell (publish rule, §3.4).
builder.Services.AddHttpClient<IPayoutReadiness, TenancyPayoutReadiness>(http => http.BaseAddress = new Uri("https+http://tenancy-api"))
    .AddSignedRequests();
// ... and how many programs its plan allows (quota, design §4).
builder.Services.AddHttpClient<IProgramQuota, TenancyProgramQuota>(http => http.BaseAddress = new Uri("https+http://tenancy-api"))
    .AddSignedRequests();
builder.Services.AddValidation();
builder.Services.AddOpenApi(options =>
{
    // Slices nest their Request/Response records; name schemas after the slice (CreateProgramRequest).
    options.CreateSchemaReferenceId = type => type.Type.IsNested
        ? type.Type.DeclaringType!.Name + type.Type.Name
        : OpenApiOptions.CreateDefaultSchemaReferenceId(type);
    // Studio handlers read the tenant from ITenantContext, not from {tenantSlug}, so the generator
    // would leave it out and the SPA's typed client could not fill it in.
    options.AddOperationTransformer<RouteParameterTransformer>();
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

// Public marketplace: every coach's published programs (a cross-tenant slice, design §3.2)
var marketplace = app.MapGroup("/api/v1/marketplace")
    .UsePublicReadScope()
    .WithTenantTransaction<CatalogDbContext>();
marketplace.MapSearchPrograms();
marketplace.MapGetPublishedProgram();

// Coach dashboard: tenant and role come from the gateway; owners and staff both manage programs
var studio = app.MapGroup("/api/v1/studio/{tenantSlug}")
    .RequireAuthorization()
    .RequireTenant()
    .WithTenantTransaction<CatalogDbContext>();
studio.MapListPrograms();
studio.MapCreateProgram();
studio.MapGetProgram();
studio.MapUpdateProgram();
studio.MapDeleteProgram();
studio.MapPublishProgram();
studio.MapProgramCover();

// Other services only (signed): published programs, public-read scope
app.MapGroup("/internal")
    .UsePublicReadScope()
    .WithTenantTransaction<CatalogDbContext>()
    .MapGetProgramOffer();
// Other services only (signed): one tenant's paid content; the handler enters that tenant itself
app.MapGroup("/internal")
    .MapGetProgramContent();
studio.MapWorkouts();

app.Run();
