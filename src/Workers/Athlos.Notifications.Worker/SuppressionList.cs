using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

namespace Athlos.Notifications.Worker;

// Addresses Communication Services could not deliver to (design §5.3), kept by tenancy-api and asked
// with signed calls, like the owners. Addresses leave the worker only as SHA-256 hashes.
public interface ISuppressionList
{
    // The addresses among these to skip; the rest may be sent to.
    Task<IReadOnlySet<string>> SuppressedAsync(IReadOnlyCollection<string> addresses, CancellationToken cancellationToken);

    Task RecordAsync(string address, string status, CancellationToken cancellationToken);
}

public static class AddressHash
{
    public static string Of(string address) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(address.Trim().ToLowerInvariant())));
}

public sealed class TenancySuppressionList(HttpClient http) : ISuppressionList
{
    private sealed record CheckResponse(List<string> Suppressed);

    public async Task<IReadOnlySet<string>> SuppressedAsync(IReadOnlyCollection<string> addresses, CancellationToken cancellationToken)
    {
        if (addresses.Count == 0)
        {
            return new HashSet<string>();
        }

        var byHash = addresses.ToLookup(AddressHash.Of);
        var query = string.Join("&", byHash.Select(h => $"hash={h.Key}"));
        var response = await http.GetFromJsonAsync<CheckResponse>($"/internal/email-suppressions?{query}", cancellationToken);
        return (response?.Suppressed ?? []).SelectMany(h => byHash[h]).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task RecordAsync(string address, string status, CancellationToken cancellationToken)
    {
        using var response = await http.PutAsJsonAsync($"/internal/email-suppressions/{AddressHash.Of(address)}", new { status }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
