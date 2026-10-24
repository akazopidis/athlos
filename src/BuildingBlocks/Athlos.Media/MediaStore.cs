using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;

namespace Athlos.Media;

// Media in Blob Storage (design §3.2 "Other isolation points", §5.1): catalog-api's program covers and
// tenancy-api's coach logos, in one private container. Each service builds its own paths.
// - Every path starts with tenants/{tenantId}/, and the tenant always comes from ITenantContext, so
//   one tenant can never write into another's prefix, and a tenant's media can be deleted by prefix.
// - The container is private. Readers get read-only SAS links for one blob, valid 10 to 20 minutes:
//   the expiry is rounded to a 10-minute window, so everyone in a window gets the same link and the
//   browser's cache (Cache-Control max-age=900 on the blob) serves the image again instead of
//   downloading it under a new address on every page.
//   In Azure they are user-delegation SAS signed with a key from the app's managed identity (no
//   account keys exist: shared-key access is disabled). Locally Azurite has only shared keys.
public sealed class MediaStore(BlobServiceClient blobs, TimeProvider time) : IDisposable
{
    public const string ContainerName = "media";
    public const long MaxImageBytes = 2 * 1024 * 1024;
    public static readonly TimeSpan ReadLinkWindow = TimeSpan.FromMinutes(10);

    private readonly BlobContainerClient _container = blobs.GetBlobContainerClient(ContainerName);
    private readonly SemaphoreSlim _keyLock = new(1, 1);
    private UserDelegationKey? _delegationKey;
    private bool _containerChecked;

    public async Task UploadAsync(string path, BinaryData content, ImageType type, CancellationToken cancellationToken)
    {
        if (!_containerChecked)
        {
            await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            _containerChecked = true;
        }

        await _container.GetBlobClient(path).UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = type.ContentType, CacheControl = "private, max-age=900" },
        }, cancellationToken);
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken) =>
        _container.GetBlobClient(path).DeleteIfExistsAsync(cancellationToken: cancellationToken);

    // Everything under tenants/{tenantId}/: a tenant's media goes with one prefix (design §3.4).
    public async Task<int> DeleteTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (!await _container.ExistsAsync(cancellationToken))
        {
            return 0;
        }

        var deleted = 0;
        await foreach (var blob in _container.GetBlobsAsync(new GetBlobsOptions { Prefix = $"tenants/{tenantId}/" }, cancellationToken))
        {
            await _container.DeleteBlobIfExistsAsync(blob.Name, cancellationToken: cancellationToken);
            deleted++;
        }

        return deleted;
    }

    public async Task<string?> ReadLinkAsync(string? path, CancellationToken cancellationToken)
    {
        if (path is null)
        {
            return null;
        }

        var blob = _container.GetBlobClient(path);
        var now = time.GetUtcNow();
        var windowStart = new DateTimeOffset(now.UtcTicks - now.UtcTicks % ReadLinkWindow.Ticks, TimeSpan.Zero);
        var expiresOn = windowStart + 2 * ReadLinkWindow;

        if (blob.CanGenerateSasUri)
        {
            return blob.GenerateSasUri(BlobSasPermissions.Read, expiresOn).ToString();
        }

        var sas = new BlobSasBuilder
        {
            BlobContainerName = ContainerName,
            BlobName = path,
            Resource = "b",
            ExpiresOn = expiresOn,
        };
        sas.SetPermissions(BlobSasPermissions.Read);
        var key = await DelegationKeyAsync(cancellationToken);
        return new BlobUriBuilder(blob.Uri) { Sas = sas.ToSasQueryParameters(key, blobs.AccountName) }.ToUri().ToString();
    }

    public void Dispose() => _keyLock.Dispose();

    // One key per day, renewed an hour before it expires; every link it signs lives 15 minutes.
    private async Task<UserDelegationKey> DelegationKeyAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (_delegationKey is { } key && key.SignedExpiresOn > now.AddHours(1))
        {
            return key;
        }

        await _keyLock.WaitAsync(cancellationToken);
        try
        {
            if (_delegationKey is null || _delegationKey.SignedExpiresOn <= now.AddHours(1))
            {
                _delegationKey = (await blobs.GetUserDelegationKeyAsync(now.AddMinutes(-5), now.AddDays(1), cancellationToken)).Value;
            }

            return _delegationKey;
        }
        finally
        {
            _keyLock.Release();
        }
    }
}

// The accepted image formats, recognised by their first bytes rather than the Content-Type the
// client claims (design §5.1: content-type allow-list and size cap for uploads).
public sealed record ImageType(string ContentType, string Extension)
{
    public static readonly ImageType Jpeg = new("image/jpeg", "jpg");
    public static readonly ImageType Png = new("image/png", "png");
    public static readonly ImageType Webp = new("image/webp", "webp");

    public static ImageType? Detect(ReadOnlySpan<byte> data) => data switch
    {
        [0xFF, 0xD8, 0xFF, ..] => Jpeg,
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => Png,
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => Webp,
        _ => null,
    };
}
