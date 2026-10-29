using Azure.Storage.Blobs;
using Testcontainers.Azurite;
using Xunit;

namespace Athlos.Testing;

// Azurite (the Blob Storage emulator) for program media. Azurite only has shared keys, so tests
// exercise the local SAS path; Azure uses user-delegation SAS (MediaStore).
public sealed class AthlosStorage : IAsyncLifetime
{
    private readonly AzuriteContainer _container = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
        .WithCommand("--skipApiVersionCheck")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    // A file in the media container, or null if it is not there.
    public async Task<BinaryData?> ReadAsync(string path)
    {
        var blob = new BlobServiceClient(ConnectionString).GetBlobContainerClient(Athlos.Media.MediaStore.ContainerName).GetBlobClient(path);
        return await blob.ExistsAsync(TestContext.Current.CancellationToken) ? (await blob.DownloadContentAsync(TestContext.Current.CancellationToken)).Value.Content : null;
    }
}
