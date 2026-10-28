using Azure.Messaging.ServiceBus;
using Testcontainers.ServiceBus;

namespace Athlos.Messaging.Tests;

// Microsoft's Service Bus emulator (the one Aspire runs locally, design §12.1), with the queues in
// emulator-config.json. Starting it accepts the emulator's and SQL Server's licence terms, which the
// project owner agreed to on 28 October 2026 for the tests. One emulator for the whole assembly.
public sealed class ServiceBusEmulator : IAsyncLifetime
{
    private readonly ServiceBusContainer _container = new ServiceBusBuilder("mcr.microsoft.com/azure-messaging/servicebus-emulator:2.0.0")
        .WithAcceptLicenseAgreement(true)
        .WithConfig(Path.Combine(AppContext.BaseDirectory, "emulator-config.json"))
        .Build();

    public ServiceBusClient Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        Client = new ServiceBusClient(_container.GetConnectionString());
    }

    public async ValueTask DisposeAsync()
    {
        if (Client is not null)
        {
            await Client.DisposeAsync();
        }

        await _container.DisposeAsync();
    }
}
