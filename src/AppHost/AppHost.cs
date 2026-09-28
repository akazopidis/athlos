var builder = DistributedApplication.CreateBuilder(args);

// The Service Bus emulator and Azurite are added with the services that need them (design §13).

// Shared by the gateway (signs) and the services (verify). Generated once and kept in user secrets.
var gatewaySigningKey = builder.AddParameter(
    "gateway-signing-key",
    new GenerateParameterDefault { MinLength = 44, Special = false },
    secret: true,
    persist: true);

// One PostgreSQL 17 server, one database per service (design §2.1, §13).
var postgres = builder.AddPostgres("postgres")
    .WithImageTag("17")
    .WithDataVolume("athlos-postgres")
    .WithLifetime(ContainerLifetime.Persistent);

// tenancy: the migrator connects as the owner; the service connects as its own non-owner role,
// so RLS applies to it exactly as in Azure. statement_timeout: design §4.
var tenancyOwnerDb = postgres.AddDatabase("tenancy-db-owner", "athlos_tenancy");
var tenancyAppPassword = builder.AddParameter(
    "tenancy-db-app-password",
    new GenerateParameterDefault { MinLength = 32, Special = false },
    secret: true,
    persist: true);
var tenancyAppDb = builder.AddConnectionString("tenancy-db", ReferenceExpression.Create(
    $"Host={postgres.Resource.PrimaryEndpoint.Property(EndpointProperty.Host)};Port={postgres.Resource.PrimaryEndpoint.Property(EndpointProperty.Port)};Database=athlos_tenancy;Username=athlos_tenancy_app;Password={tenancyAppPassword};Maximum Pool Size=3;Options=-c statement_timeout=5000"));

var migrator = builder.AddProject<Projects.Athlos_DbMigrator>("db-migrator")
    .WithReference(tenancyOwnerDb)
    .WithEnvironment("Databases__Tenancy__AppPassword", tenancyAppPassword)
    .WaitFor(tenancyOwnerDb);

// Entra External ID (design §5). Locally the dev app registrations are used; set them with
// dotnet user-secrets in src/AppHost: Entra:Authority, Entra:ApiClientId, Entra:SpaClientId.
// Without them everything runs, signed out.
var entraAuthority = builder.Configuration["Entra:Authority"] ?? "";
var entraApiClientId = builder.Configuration["Entra:ApiClientId"] ?? "";
var entraSpaClientId = builder.Configuration["Entra:SpaClientId"] ?? "";
var apiScope = entraApiClientId.Length > 0 ? $"api://{entraApiClientId}/access_as_user" : "";

var tenancyApi = builder.AddProject<Projects.Athlos_Tenancy_Api>("tenancy-api")
    .WithEnvironment("Auth__Authority", entraAuthority)
    .WithEnvironment("Auth__Audience", entraApiClientId)
    .WithEnvironment("GatewaySignature__Key", gatewaySigningKey)
    .WithReference(tenancyAppDb)
    .WaitForCompletion(migrator);

var gateway = builder.AddProject<Projects.Athlos_Gateway>("gateway")
    .WithEnvironment("GatewaySignature__Key", gatewaySigningKey)
    .WithEnvironment("Auth__Authority", entraAuthority)
    .WithEnvironment("Auth__Audience", entraApiClientId)
    .WithEnvironment("Spa__EntraClientId", entraSpaClientId)
    .WithEnvironment("Spa__EntraAuthority", entraAuthority)
    .WithEnvironment("Spa__ApiScope", apiScope)
    .WithReference(tenancyApi)
    .WaitFor(tenancyApi);

// Locally the Vite dev server serves the SPA and proxies API calls to the gateway (src/web/vite.config.ts).
// Fixed port: it is the redirect URI registered on the dev SPA app registration.
builder.AddViteApp("web", "../web")
    .WithEndpoint("http", endpoint => endpoint.Port = 5173)
    .WithReference(gateway)
    .WaitFor(gateway);

builder.Build().Run();
