var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL, the Service Bus emulator and Azurite are added with the services that need them (design §13).

var tenancyApi = builder.AddProject<Projects.Athlos_Tenancy_Api>("tenancy-api");

var gateway = builder.AddProject<Projects.Athlos_Gateway>("gateway")
    .WithReference(tenancyApi)
    .WaitFor(tenancyApi);

// Locally the Vite dev server serves the SPA and proxies API calls to the gateway (src/web/vite.config.ts).
builder.AddViteApp("web", "../web")
    .WithReference(gateway)
    .WaitFor(gateway);

builder.Build().Run();
