using System.Text.Json.Serialization;
using Athlos.Tenancy;
using Athlos.Tenancy.Api.Data;
using Athlos.Tenancy.Api.Features.Account;
using Athlos.Tenancy.Api.Features.Internal;
using Athlos.Tenancy.Api.Features.Plans;
using Athlos.Tenancy.Api.Features.Storefront;
using Athlos.Tenancy.Api.Features.Tenants;
using Athlos.Tenancy.Api.Features.Workspace;
using Athlos.Tenancy.Data;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddGatewaySignature(builder.Configuration);
builder.Services.AddAthlosAuthentication(builder.Configuration);
builder.Services.AddTenantContext();
builder.Services.AddTenantDbContext<TenancyDbContext>(options => options
    .UseNpgsql(builder.Configuration.GetConnectionString("tenancy-db"))
    .UseSnakeCaseNamingConvention());
builder.Services.AddValidation();
builder.Services.AddOpenApi();
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
    .MapGetStorefront();

// Signed-in user, no tenant yet
app.MapCreateTenant();
var me = app.MapGroup("/api/v1/me")
    .RequireAuthorization()
    .WithTenantTransaction<TenancyDbContext>();
me.MapListMyMemberships();
me.MapUpsertMyProfile();

// Coach dashboard: tenant and role come from the gateway
var studio = app.MapGroup("/api/v1/studio/{tenantSlug}")
    .RequireAuthorization()
    .RequireTenant()
    .WithTenantTransaction<TenancyDbContext>();
studio.MapGetWorkspace();
studio.MapUpdateWorkspace();

// Gateway only
app.MapLookupMembership();

app.Run();
