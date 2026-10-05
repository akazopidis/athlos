using System.Net;
using System.Net.Http.Json;
using Athlos.Tenancy;
using Microsoft.Extensions.Caching.Hybrid;

namespace Athlos.Gateway;

// A coach's plan limits, by workspace slug, from tenancy-api (design §4). Cached for a minute like
// memberships (including "no such active workspace"), so a plan change applies within about 60 s.
public sealed class TenantPlans(HttpClient http, GatewaySignature signature, HybridCache cache)
{
    public sealed record Limits(Guid TenantId, string Plan, int RequestsPerSecond);

    private sealed record Response(Guid TenantId, PlanDetails Plan);

    private sealed record PlanDetails(string Name, int RequestsPerSecond);

    private static readonly HybridCacheEntryOptions CacheFor = new() { Expiration = TimeSpan.FromSeconds(60), LocalCacheExpiration = TimeSpan.FromSeconds(60) };

    public async Task<Limits?> FindAsync(string slug, CancellationToken cancellationToken) =>
        (await cache.GetOrCreateAsync(
            $"tenant-plan:{slug}",
            slug,
            async (key, token) => new Cached(await FetchAsync(key, token)),
            CacheFor,
            cancellationToken: cancellationToken)).Limits;

    private async Task<Limits?> FetchAsync(string slug, CancellationToken cancellationToken)
    {
        var path = $"/internal/tenants/{slug}/plan";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        var signed = signature.Sign("GET", path, tenantId: null, userRole: null);
        request.Headers.Add(GatewaySignature.TimestampHeader, signed.Timestamp);
        request.Headers.Add(GatewaySignature.SignatureHeader, signed.Signature);

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var found = await response.Content.ReadFromJsonAsync<Response>(cancellationToken);
        return found is null ? null : new Limits(found.TenantId, found.Plan.Name, found.Plan.RequestsPerSecond);
    }

    // A wrapper, so that "not found" is cached too.
    private sealed record Cached(Limits? Limits);
}
