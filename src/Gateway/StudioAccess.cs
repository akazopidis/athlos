using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Athlos.Tenancy;
using Microsoft.Extensions.Caching.Hybrid;
using Yarp.ReverseProxy.Transforms;

namespace Athlos.Gateway;

// Layer 1 for the coach dashboard (design §3.2): every /api/v1/studio/{slug}/... request is from a
// signed-in member of that workspace, or it is a 404. The gateway then tells the service which
// tenant and role apply, in headers it signs.
public static partial class StudioAccess
{
    public sealed record Membership(Guid TenantId, TenantRole Role, string Plan);

    private static readonly PathString StudioPrefix = "/api/v1/studio";

    // Proxy pipeline step, after routing and before the request is forwarded.
    public static async Task ResolveMembership(HttpContext context, Func<Task> next)
    {
        if (!context.Request.Path.StartsWithSegments(StudioPrefix, out var rest))
        {
            await next();
            return;
        }

        if (TenantContextMiddleware.UserIdOf(context.User) is not { } userId)
        {
            await Problem(context, StatusCodes.Status401Unauthorized);
            return;
        }

        var slug = rest.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var membership = slug is not null && Slug().IsMatch(slug)
            ? await context.RequestServices.GetRequiredService<MembershipLookup>().FindAsync(slug, userId, context.RequestAborted)
            : null;

        // Not a member, not a workspace, or not active: all the same 404, never 403 (design §3.3).
        if (membership is null)
        {
            await Problem(context, StatusCodes.Status404NotFound);
            return;
        }

        context.Items[typeof(Membership)] = membership;
        await next();
    }

    // Request transform, after the caller's copies are stripped and before signing.
    public static ValueTask AddTrustedHeaders(RequestTransformContext context)
    {
        if (context.HttpContext.Items[typeof(Membership)] is Membership membership)
        {
            context.ProxyRequest.Headers.Add(TrustedHeaders.TenantId, membership.TenantId.ToString());
            context.ProxyRequest.Headers.Add(TrustedHeaders.UserRole, membership.Role.ToString());
        }

        return ValueTask.CompletedTask;
    }

    private static async Task Problem(HttpContext context, int statusCode)
    {
        context.Response.StatusCode = statusCode;
        await context.RequestServices.GetRequiredService<IProblemDetailsService>()
            .WriteAsync(new ProblemDetailsContext { HttpContext = context });
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Slug();
}

// Asks tenancy-api who the caller is in a workspace, and caches the answer (including "nobody")
// for a minute, so a role change or removal takes effect within about 60 s (design §3.2).
public sealed class MembershipLookup(HttpClient http, GatewaySignature signature, HybridCache cache)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly HybridCacheEntryOptions CacheFor = new() { Expiration = TimeSpan.FromSeconds(60), LocalCacheExpiration = TimeSpan.FromSeconds(60) };

    public async Task<StudioAccess.Membership?> FindAsync(string slug, Guid userId, CancellationToken cancellationToken) =>
        (await cache.GetOrCreateAsync(
            $"studio-membership:{slug}:{userId}",
            (slug, userId),
            async (key, token) => new CachedMembership(await FetchAsync(key.slug, key.userId, token)),
            CacheFor,
            cancellationToken: cancellationToken)).Membership;

    private async Task<StudioAccess.Membership?> FetchAsync(string slug, Guid userId, CancellationToken cancellationToken)
    {
        var path = $"/internal/tenants/{slug}/members/{userId}";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        // Service-to-service calls are signed like proxied ones; tenancy-api rejects anything else.
        var signed = signature.Sign("GET", path, tenantId: null, userRole: null);
        request.Headers.Add(GatewaySignature.TimestampHeader, signed.Timestamp);
        request.Headers.Add(GatewaySignature.SignatureHeader, signed.Signature);

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StudioAccess.Membership>(Json, cancellationToken);
    }

    // A wrapper, so that "not a member" is cached too.
    private sealed record CachedMembership(StudioAccess.Membership? Membership);
}
