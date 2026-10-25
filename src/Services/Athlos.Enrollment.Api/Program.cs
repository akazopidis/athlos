using System.Text.Json.Serialization;
using Athlos.Enrollment.Api.Data;
using Athlos.Enrollment.Api.Features.Enrollments;
using Athlos.Enrollment.Api.Features.MyPrograms;
using Athlos.Enrollment.Api.Features.Reviews;
using Athlos.Messaging;
using Athlos.Messaging.Contracts;
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
builder.Services.AddTenantDbContext<EnrollmentDbContext>(options => options
    .UseNpgsql(
        builder.Configuration.GetConnectionString("enrollment-db"),
        npgsql => npgsql.ConfigureDataSource(dataSource => dataSource.UseEntraTokenIfNoPassword(builder.Configuration["AZURE_CLIENT_ID"])))
    .UseSnakeCaseNamingConvention());
builder.Services.AddSingleton(TimeProvider.System);

// OrderPaid arrives from ordering-api through Service Bus (design §2.2).
builder.AddQueueConsumer<OrderPaid, EnrollOnOrderPaid>(OrderPaid.Queue);
builder.AddQueueConsumer<OrderRefunded, RevokeOnOrderRefunded>(OrderRefunded.Queue);
// The purchase confirmation email leaves through the outbox to the notifications queue (design §5.3).
builder.AddOutbox<EnrollmentDbContext>();

// The paid content stays in catalog-api; enrollment asks for it with a signed call.
builder.Services.AddHttpClient<IProgramContent, CatalogProgramContent>(http => http.BaseAddress = new Uri("https+http://catalog-api"))
    .AddSignedRequests();
// ... and keeps each program's rating there in step with its reviews (design §2.4).
builder.Services.AddHttpClient<IProgramRatings, CatalogProgramRatings>(http => http.BaseAddress = new Uri("https+http://catalog-api"))
    .AddSignedRequests();

builder.Services.AddValidation();
builder.Services.AddOpenApi(options =>
    // Slices nest their Request/Response records; name schemas after the slice (GetMyProgramResponse).
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

// Customer: the programs they bought, across coaches. Reads are their own rows (MyEnrollments);
// the program page and progress writes manage their own transactions around the call to catalog.
var me = app.MapGroup("/api/v1/me").RequireAuthorization();
me.MapGroup("")
    .WithTenantTransaction<EnrollmentDbContext>()
    .MapListMyPrograms();
me.MapGetMyProgram();
me.MapWorkoutCompletion();
me.MapMyReview();

// Public: a program's reviews on its page, and reporting one (cross-tenant slices, design §3.2); both ask
// catalog whether the program is on sale first, then open their own transaction
var marketplace = app.MapGroup("/api/v1/marketplace")
    .UsePublicReadScope();
marketplace.MapProgramReviews();
marketplace.MapReportReview();

// Platform admin (Entra app role Platform.Admin): reported reviews, hidden or kept (design §2.4)
app.MapAdminReviews();

// Coach dashboard: the workspace's reviews and the coach's replies; tenant and role come from the gateway
app.MapGroup("/api/v1/studio/{tenantSlug}")
    .RequireAuthorization()
    .RequireTenant()
    .WithTenantTransaction<EnrollmentDbContext>()
    .MapStudioReviews();

// Tenancy-api only (signed): erase one deleted workspace's enrollments (design §3.4)
app.MapEraseTenant();
// Tenancy-api only (signed): one workspace's customers per program and reviews for its owner's export (design §5.4)
app.MapExportTenant();
// Tenancy-api only (signed): erase a deleted account's enrollments (design §5.2)
app.MapEraseCustomer();
// Tenancy-api only (signed): a customer's programs, progress and reviews for their data export (design §5.4)
app.MapExportCustomer();
// Catalog-api only (signed): a program on sale got a new workout; its customers are emailed (design §5.3)
app.MapProgramUpdated();

app.Run();
