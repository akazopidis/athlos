var builder = DistributedApplication.CreateBuilder(args);

// Services, PostgreSQL, the Service Bus emulator and Azurite are added here
// as they are built (see docs/design.md §13 and §15).

builder.Build().Run();
