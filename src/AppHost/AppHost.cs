var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL, the Service Bus emulator and Azurite are added with the services that need them (design §13).

// Shared by the gateway (signs) and the services (verify). Generated once and kept in user secrets.
var gatewaySigningKey = builder.AddParameter(
    "gateway-signing-key",
    new GenerateParameterDefault { MinLength = 44, Special = false },
    secret: true,
    persist: true);

var tenancyApi = builder.AddProject<Projects.Athlos_Tenancy_Api>("tenancy-api")
    .WithEnvironment("GatewaySignature__Key", gatewaySigningKey);

var gateway = builder.AddProject<Projects.Athlos_Gateway>("gateway")
    .WithEnvironment("GatewaySignature__Key", gatewaySigningKey)
    .WithReference(tenancyApi)
    .WaitFor(tenancyApi);

// Locally the Vite dev server serves the SPA and proxies API calls to the gateway (src/web/vite.config.ts).
builder.AddViteApp("web", "../web")
    .WithReference(gateway)
    .WaitFor(gateway);

builder.Build().Run();
