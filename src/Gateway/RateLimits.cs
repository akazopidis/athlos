using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Athlos.Gateway;

// Noisy-neighbor protection at the gateway (design §4, layers 1 and 2): one coach's traffic spike
// must not slow down the others.
//
// - Each coach's studio and storefront traffic shares one token bucket, sized by the coach's plan
//   (Free 20 req/s, Pro 100 req/s). Studio requests count only once the caller is a member, so a
//   stranger cannot spend a coach's studio budget.
// - In front of a coach's bucket, each visitor of the coach's storefront and programs has one at half
//   the coach's rate (by account when signed in, by IP otherwise), so one client flooding a storefront
//   cannot spend the whole budget and lock the coach's other visitors out (design §4). The studio is
//   not limited per visitor: its callers are the coach's own team.
// - Cover uploads and searches also hold one of a few concurrent slots per coach, so slow work
//   cannot pile up.
// - Traffic that belongs to no coach (the cross-coach marketplace search, unknown addresses) is
//   limited per client IP instead.
// - Starting a checkout opens a Stripe payment page and writes pending orders, so it is limited per
//   signed-in customer (per client IP without a sign-in, which checkout then refuses anyway):
//   CheckoutsPerMinute, 10 by default. Checking a discount code shares the same bucket, so codes
//   cannot be guessed quickly (design §2.4).
// - A signed-in customer's other writes (reviews, saved programs, progress, reports: anything but GET
//   under /api/v1/me, and reporting a review) share a per-customer bucket, CustomerWritesPerMinute, 60
//   by default, so one account cannot flood the services with writes. Reads stay unlimited.
// - Over the limit: 429 with Retry-After, and athlos.ratelimit.rejected counts it per coach.
//
// The buckets live in each replica's memory, so each replica enforces its share of every limit: the
// limit divided by GatewayReplicas (the gateway's maxReplicas), rounded up (decided 26 October 2026).
// At full scale the replicas together allow the limit; while fewer run, a client gets less than its
// limit, never more (design §4). A shared store (Redis) would be exact at any scale, at a cost.
public sealed partial class RateLimits(IOptions<RateLimits.Options> configured)
{
    public sealed class Options
    {
        // False only for the "limits off" run of the noisy-neighbor experiment.
        public bool Enabled { get; set; } = true;

        public int PerClientRequestsPerSecond { get; set; } = 20;

        public int ConcurrentHeavyRequests { get; set; } = 4;

        public int CheckoutsPerMinute { get; set; } = 10;

        public int CustomerWritesPerMinute { get; set; } = 60;

        // The gateway's maxReplicas (infra/env.bicep): each replica enforces its share of every limit.
        public int GatewayReplicas { get; set; } = 1;

        public int Share(int limit) => Math.Max(1, (int)Math.Ceiling(limit / (double)Math.Max(1, GatewayReplicas)));
    }

    private sealed record Partition(string Key, int RequestsPerSecond, string Scope, string? Tenant, string? Plan, string? Slug = null);

    private static readonly PathString StudioPrefix = "/api/v1/studio";
    private static readonly PathString CoachesPrefix = "/api/v1/marketplace/coaches";
    private static readonly PathString ProgramsPrefix = "/api/v1/marketplace/programs";
    private static readonly PathString CheckoutPath = "/api/v1/orders/checkout";
    private static readonly PathString DiscountPath = "/api/v1/orders/discount";
    private static readonly PathString MePrefix = "/api/v1/me";

    private static readonly Meter Meter = new("Athlos.Gateway");
    private static readonly Counter<long> Rejected = Meter.CreateCounter<long>(
        "athlos.ratelimit.rejected", description: "Requests refused with 429, per coach (tenant) or per client for unattributed traffic.");

    // One limiter per partition key; the plan's rate is part of the key, so a plan change starts a new
    // bucket. Idle partitions are dropped by the partitioned limiter, so per-client keys do not pile up.
    private readonly PartitionedRateLimiter<Partition> _requests = PartitionedRateLimiter.Create<Partition, string>(p =>
        RateLimitPartition.GetTokenBucketLimiter(p.Key, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = p.RequestsPerSecond,
            TokensPerPeriod = p.RequestsPerSecond,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));

    // Per customer, a minute's worth of checkouts (or of other writes) at once, refilled over the minute.
    private readonly PartitionedRateLimiter<Partition> _checkouts = PartitionedRateLimiter.Create<Partition, string>(p =>
        RateLimitPartition.GetTokenBucketLimiter(p.Key, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = p.RequestsPerSecond,
            TokensPerPeriod = p.RequestsPerSecond,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));

    private readonly PartitionedRateLimiter<Partition> _heavy = PartitionedRateLimiter.Create<Partition, string>(p =>
        RateLimitPartition.GetConcurrencyLimiter(p.Key, _ => new ConcurrencyLimiterOptions
        {
            PermitLimit = configured.Value.ConcurrentHeavyRequests,
            QueueLimit = 0,
        }));

    // Proxy pipeline step, after StudioAccess.ResolveMembership.
    public static async Task Apply(HttpContext context, Func<Task> next)
    {
        var options = context.RequestServices.GetRequiredService<IOptionsMonitor<Options>>().CurrentValue;
        if (options.Enabled && HttpMethods.IsPost(context.Request.Method)
            && (context.Request.Path.Equals(CheckoutPath, StringComparison.OrdinalIgnoreCase) || context.Request.Path.Equals(DiscountPath, StringComparison.OrdinalIgnoreCase)))
        {
            var customer = Athlos.Tenancy.TenantContextMiddleware.UserIdOf(context.User)?.ToString()
                ?? $"client:{context.Connection.RemoteIpAddress}";
            var checkout = new Partition($"checkout:{customer}:{options.CheckoutsPerMinute}", options.Share(options.CheckoutsPerMinute), "checkout", Tenant: null, Plan: null);
            using var allowed = context.RequestServices.GetRequiredService<RateLimits>()._checkouts.AttemptAcquire(checkout);
            if (!allowed.IsAcquired)
            {
                await Reject(context, checkout, allowed, reason: "checkout");
                return;
            }

            await next();
            return;
        }

        if (options.Enabled && IsCustomerWrite(context.Request) && Athlos.Tenancy.TenantContextMiddleware.UserIdOf(context.User) is { } writer)
        {
            var writes = new Partition($"writes:{writer}:{options.CustomerWritesPerMinute}", options.Share(options.CustomerWritesPerMinute), "writes", Tenant: null, Plan: null);
            using var allowed = context.RequestServices.GetRequiredService<RateLimits>()._checkouts.AttemptAcquire(writes);
            if (!allowed.IsAcquired)
            {
                await Reject(context, writes, allowed, reason: "writes");
                return;
            }
        }

        var partition = options.Enabled ? await AttributeAsync(context, options) : null;
        if (partition is null)
        {
            await next();
            return;
        }

        var limits = context.RequestServices.GetRequiredService<RateLimits>();
        if (partition.Scope == "tenant" && !context.Request.Path.StartsWithSegments(StudioPrefix))
        {
            var visitor = Athlos.Tenancy.TenantContextMiddleware.UserIdOf(context.User)?.ToString()
                ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var share = Math.Max(1, partition.RequestsPerSecond / 2);
            var visitorPartition = partition with { Key = $"visitor:{partition.Tenant}:{visitor}:{share}", RequestsPerSecond = share, Scope = "visitor" };
            using var visitorLease = limits._requests.AttemptAcquire(visitorPartition);
            if (!visitorLease.IsAcquired)
            {
                await Reject(context, visitorPartition, visitorLease, reason: "visitor");
                return;
            }
        }

        using var lease = limits._requests.AttemptAcquire(partition);
        if (!lease.IsAcquired)
        {
            await Reject(context, partition, lease, reason: "rate");
            return;
        }

        if (!IsHeavy(context.Request))
        {
            await next();
            return;
        }

        using var slot = limits._heavy.AttemptAcquire(partition);
        if (!slot.IsAcquired)
        {
            await Reject(context, partition, slot, reason: "concurrency");
            return;
        }

        await next();
    }

    // A customer's write: anything but a read under /api/v1/me, or reporting a review.
    private static bool IsCustomerWrite(HttpRequest request) =>
        !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)
        && (request.Path.StartsWithSegments(MePrefix)
            || (request.Path.StartsWithSegments(ProgramsPrefix) && request.Path.Value!.EndsWith("/report", StringComparison.OrdinalIgnoreCase)));

    // Whose budget a request spends: the coach in the path (studio, storefront) or in ?coach= (a
    // storefront's program list), otherwise the client.
    private static async Task<Partition?> AttributeAsync(HttpContext context, Options options)
    {
        var request = context.Request;
        string? slug = null;
        if (request.Path.StartsWithSegments(StudioPrefix, out var studioRest))
        {
            // ResolveMembership let it through, so the caller is a member of this workspace.
            slug = FirstSegment(studioRest);
        }
        else if (request.Path.StartsWithSegments(CoachesPrefix, out var coachRest))
        {
            slug = FirstSegment(coachRest);
        }
        else if (request.Path.StartsWithSegments(ProgramsPrefix))
        {
            slug = request.Query["coach"].FirstOrDefault();
        }
        else
        {
            return null;
        }

        if (slug is not null && Slug().IsMatch(slug) && await FindPlanAsync(context, slug) is { } plan)
        {
            // Tenant-aware telemetry (design §6): the gateway's request span names the coach and plan,
            // so the workbook can chart each coach's traffic, latency and 429s.
            System.Diagnostics.Activity.Current?.SetTag(Athlos.Tenancy.TelemetryTags.TenantId, plan.TenantId.ToString());
            System.Diagnostics.Activity.Current?.SetTag(Athlos.Tenancy.TelemetryTags.TenantSlug, slug);
            System.Diagnostics.Activity.Current?.SetTag(Athlos.Tenancy.TelemetryTags.TenantPlan, plan.Plan);
            return new Partition($"tenant:{plan.TenantId}:{plan.RequestsPerSecond}", options.Share(plan.RequestsPerSecond), "tenant", plan.TenantId.ToString(), plan.Plan, slug);
        }

        var client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return new Partition($"client:{client}:{options.PerClientRequestsPerSecond}", options.Share(options.PerClientRequestsPerSecond), "client", Tenant: null, Plan: null);
    }

    // The limiter never fails a request itself: if tenancy-api cannot answer (for example while it
    // starts from zero replicas), the request is limited per client instead and goes on, and the
    // answer is not cached, so the next request asks again.
    private static async Task<TenantPlans.Limits?> FindPlanAsync(HttpContext context, string slug)
    {
        try
        {
            return await context.RequestServices.GetRequiredService<TenantPlans>().FindAsync(slug, context.RequestAborted);
        }
        catch (Exception e) when (!context.RequestAborted.IsCancellationRequested)
        {
            LogPlanLookupFailed(context.RequestServices.GetRequiredService<ILogger<RateLimits>>(), slug, e);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plan lookup for {Slug} failed; limiting per client instead")]
    private static partial void LogPlanLookupFailed(ILogger logger, string slug, Exception exception);

    // Cover uploads and text searches do the most work per request.
    private static bool IsHeavy(HttpRequest request) =>
        (HttpMethods.IsPut(request.Method) && request.Path.Value?.EndsWith("/cover", StringComparison.Ordinal) == true)
        || (request.Path.StartsWithSegments(ProgramsPrefix) && !string.IsNullOrWhiteSpace(request.Query["q"]));

    private static async Task Reject(HttpContext context, Partition partition, RateLimitLease lease, string reason)
    {
        Rejected.Add(1,
            new KeyValuePair<string, object?>("scope", partition.Scope),
            new KeyValuePair<string, object?>("tenant", partition.Tenant),
            new KeyValuePair<string, object?>(Athlos.Tenancy.TelemetryTags.TenantSlug, partition.Slug),
            new KeyValuePair<string, object?>("plan", partition.Plan),
            new KeyValuePair<string, object?>("reason", reason));

        var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)) : 1;
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = { Title = "Too many requests. Try again in a moment." },
        });
    }

    private static string? FirstSegment(PathString rest) =>
        rest.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Slug();
}
