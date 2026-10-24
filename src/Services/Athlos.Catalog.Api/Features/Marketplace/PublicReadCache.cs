using Microsoft.AspNetCore.OutputCaching;

namespace Athlos.Catalog.Api.Features.Marketplace;

// A 10-second output cache for the public marketplace reads (design §4): the program search and
// program pages are the same for every visitor, so a burst of visitors costs one database read per
// address every 10 seconds instead of one per request. The stress test (docs/evaluation/stress.md)
// found catalog-api's single replica running out of CPU on exactly this work.
// - Only these public-read routes use it: nothing here depends on who asks (no tenant, no user), so
//   a signed-in visitor's Authorization header, which the built-in policy would refuse to cache, does
//   not matter. Studio and internal routes are never cached.
// - The key is the full address, query string included. Only 200 responses are stored.
// - The cache lives in each replica's memory (no Redis, design §14); a change shows within 10 seconds.
//   Marketplace:CacheSeconds sets the duration (0 turns it off, as the integration tests do).
//   Unsigned requests are refused by UseGatewayOnly before the cache is consulted.
public sealed class PublicReadCache(TimeSpan duration) : IOutputCachePolicy
{
    public const string Name = "public-read";

    ValueTask IOutputCachePolicy.CacheRequestAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        var get = duration > TimeSpan.Zero
            && (HttpMethods.IsGet(context.HttpContext.Request.Method) || HttpMethods.IsHead(context.HttpContext.Request.Method));
        context.EnableOutputCaching = true;
        context.AllowCacheLookup = get;
        context.AllowCacheStorage = get;
        context.AllowLocking = true;
        context.ResponseExpirationTimeSpan = duration;
        context.CacheVaryByRules.QueryKeys = "*";
        return ValueTask.CompletedTask;
    }

    ValueTask IOutputCachePolicy.ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellation) => ValueTask.CompletedTask;

    ValueTask IOutputCachePolicy.ServeResponseAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        if (context.HttpContext.Response.StatusCode != StatusCodes.Status200OK)
        {
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }
}
