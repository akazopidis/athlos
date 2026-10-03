var builder = DistributedApplication.CreateBuilder(args);


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

var migrator = builder.AddProject<Projects.Athlos_DbMigrator>("db-migrator");

// Each service gets its own database. The migrator connects as the owner; the service connects as
// its own non-owner role, so RLS applies to it exactly as in Azure. statement_timeout: design §4.
IResourceBuilder<IResourceWithConnectionString> ServiceDatabase(string service)
{
    var ownerDb = postgres.AddDatabase($"{service}-db-owner", $"athlos_{service}");
    var appPassword = builder.AddParameter(
        $"{service}-db-app-password",
        new GenerateParameterDefault { MinLength = 32, Special = false },
        secret: true,
        persist: true);

    var section = char.ToUpperInvariant(service[0]) + service[1..];
    migrator.WithReference(ownerDb)
        .WithEnvironment($"Databases__{section}__AppPassword", appPassword)
        .WaitFor(ownerDb);

    return builder.AddConnectionString($"{service}-db", ReferenceExpression.Create(
        $"Host={postgres.Resource.PrimaryEndpoint.Property(EndpointProperty.Host)};Port={postgres.Resource.PrimaryEndpoint.Property(EndpointProperty.Port)};Database=athlos_{service};Username=athlos_{service}_app;Password={appPassword};Maximum Pool Size=3;Options=-c statement_timeout=5000"));
}

var tenancyDb = ServiceDatabase("tenancy");
var catalogDb = ServiceDatabase("catalog");
var orderingDb = ServiceDatabase("ordering");
var enrollmentDb = ServiceDatabase("enrollment");

// Service Bus emulator with the queues of design §7 (Basic tier in Azure: queues only).
var messaging = builder.AddAzureServiceBus("messaging").RunAsEmulator();
messaging.AddServiceBusQueue("order-paid");
messaging.AddServiceBusQueue("notifications");

// Entra External ID (design §5). Locally the dev app registrations are used; set them with
// dotnet user-secrets in src/AppHost: Entra:Authority, Entra:ApiClientId, Entra:SpaClientId.
// Without them everything runs, signed out.
var entraAuthority = builder.Configuration["Entra:Authority"] ?? "";
var entraApiClientId = builder.Configuration["Entra:ApiClientId"] ?? "";
var entraSpaClientId = builder.Configuration["Entra:SpaClientId"] ?? "";
var apiScope = entraApiClientId.Length > 0 ? $"api://{entraApiClientId}/access_as_user" : "";

// Stripe test mode (design §15 phase 4). Optional locally: dotnet user-secrets set Stripe:SecretKey
// in src/AppHost. Without it, payout onboarding answers 503.
var stripeSecretKey = builder.Configuration["Stripe:SecretKey"] ?? "";
// From `stripe listen --forward-to http://localhost:5100/webhooks/stripe` (it prints the whsec_ secret).
var stripeWebhookSecret = builder.Configuration["Stripe:WebhookSecret"] ?? "";

var tenancyApi = builder.AddProject<Projects.Athlos_Tenancy_Api>("tenancy-api")
    .WithEnvironment("Stripe__SecretKey", stripeSecretKey)
    // Where Stripe sends coaches back after onboarding: the SPA (Vite locally).
    .WithEnvironment("App__PublicUrl", "http://localhost:5173")
    .WithEnvironment("Auth__Authority", entraAuthority)
    .WithEnvironment("Auth__Audience", entraApiClientId)
    .WithEnvironment("GatewaySignature__Key", gatewaySigningKey)
    .WithReference(tenancyDb)
    .WithReference(messaging)
    .WaitFor(messaging)
    .WaitForCompletion(migrator);

// Program media in Azurite, the Blob Storage emulator (design §13).
var media = builder.AddAzureStorage("storage")
    .RunAsEmulator(azurite => azurite.WithDataVolume("athlos-azurite").WithLifetime(ContainerLifetime.Persistent))
    .AddBlobs("media");

var catalogApi = builder.AddProject<Projects.Athlos_Catalog_Api>("catalog-api")
    .WithReference(tenancyApi)
    .WithReference(media)
    .WaitFor(media)
    .WithEnvironment("Auth__Authority", entraAuthority)
    .WithEnvironment("Auth__Audience", entraApiClientId)
    .WithEnvironment("GatewaySignature__Key", gatewaySigningKey)
    .WithReference(catalogDb)
    .WaitForCompletion(migrator);

var orderingApi = builder.AddProject<Projects.Athlos_Ordering_Api>("ordering-api")
    .WithEnvironment("Auth__Authority", entraAuthority)
    .WithEnvironment("Auth__Audience", entraApiClientId)
    .WithEnvironment("GatewaySignature__Key", gatewaySigningKey)
    .WithEnvironment("Stripe__SecretKey", stripeSecretKey)
    .WithEnvironment("Stripe__WebhookSecret", stripeWebhookSecret)
    .WithEnvironment("App__PublicUrl", "http://localhost:5173")
    .WithReference(orderingDb)
    .WithReference(messaging)
    .WithReference(catalogApi)
    .WithReference(tenancyApi)
    .WaitFor(messaging)
    .WaitForCompletion(migrator);

// Consumes OrderPaid from the order-paid queue and serves the customer's programs (design §2.1).
var enrollmentApi = builder.AddProject<Projects.Athlos_Enrollment_Api>("enrollment-api")
    .WithEnvironment("Auth__Authority", entraAuthority)
    .WithEnvironment("Auth__Audience", entraApiClientId)
    .WithEnvironment("GatewaySignature__Key", gatewaySigningKey)
    .WithEnvironment("App__PublicUrl", "http://localhost:5173")
    .WithReference(enrollmentDb)
    .WithReference(messaging)
    .WithReference(catalogApi)
    .WaitFor(messaging)
    .WaitForCompletion(migrator);

// Sends the emails of design §5.3. Locally nothing leaves the machine: emails are written to
// src/AppHost/.emails (git-ignored) and the log names only the subject.
builder.AddProject<Projects.Athlos_Notifications_Worker>("notifications-worker")
    .WithEnvironment("GatewaySignature__Key", gatewaySigningKey)
    .WithEnvironment("Email__Mode", "Sink")
    .WithEnvironment("Email__SinkFolder", Path.Combine(builder.AppHostDirectory, ".emails"))
    .WithReference(messaging)
    .WithReference(tenancyApi)
    .WaitFor(messaging);

var gateway = builder.AddProject<Projects.Athlos_Gateway>("gateway")
    .WithEnvironment("GatewaySignature__Key", gatewaySigningKey)
    .WithEnvironment("Auth__Authority", entraAuthority)
    .WithEnvironment("Auth__Audience", entraApiClientId)
    .WithEnvironment("Spa__EntraClientId", entraSpaClientId)
    .WithEnvironment("Spa__EntraAuthority", entraAuthority)
    .WithEnvironment("Spa__ApiScope", apiScope)
    .WithReference(tenancyApi)
    .WithReference(catalogApi)
    .WithReference(orderingApi)
    .WithReference(enrollmentApi)
    .WaitFor(tenancyApi)
    .WaitFor(catalogApi)
    .WaitFor(orderingApi)
    .WaitFor(enrollmentApi);

// Locally the Vite dev server serves the SPA and proxies API calls to the gateway (src/web/vite.config.ts).
// Fixed port: it is the redirect URI registered on the dev SPA app registration.
builder.AddViteApp("web", "../web")
    .WithEndpoint("http", endpoint => endpoint.Port = 5173)
    .WithReference(gateway)
    .WaitFor(gateway);

builder.Build().Run();
