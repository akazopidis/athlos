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
// - Cover uploads and searches also hold one of a few concurrent slots per coach, so slow work
//   cannot pile up.
// - Traffic that belongs to no coach (the cross-coach marketplace search, unknown addresses) is
//   limited per client IP instead.
// - Over the limit: 429 with Retry-After, and athlos.ratelimit.rejected counts it per coach.
//
// The buckets live in this replica's memory: with N gateway replicas a coach effectively gets N
// times its limit (design §4, known limitation; the gateway is capped at 2 replicas).
public sealed partial class RateLimits(IOptions<RateLimits.Options> configured)
{
    public sealed class Options
    {
        // False only for the "limits off" run of the noisy-neighbor experiment.
        public bool Enabled { get; set; } = true;

        public int PerClientRequestsPerSecond { get; set; } = 20;

        public int ConcurrentHeavyRequests { get; set; } = 4;
    }

    private sealed record Partition(string Key, int RequestsPerSecond, string Scope, string? Tenant, string? Plan);

    private static readonly PathString StudioPrefix = "/api/v1/studio";
    private static readonly PathString CoachesPrefix = "/api/v1/marketplace/coaches";
    private static readonly PathString ProgramsPrefix = "/api/v1/marketplace/programs";

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
        var partition = options.Enabled ? await AttributeAsync(context, options) : null;
        if (partition is null)
        {
            await next();
            return;
        }

        var limits = context.RequestServices.GetRequiredService<RateLimits>();
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
            System.Diagnostics.Activity.Current?.SetTag(Athlos.Tenancy.TelemetryTags.TenantPlan, plan.Plan);
            return new Partition($"tenant:{plan.TenantId}:{plan.RequestsPerSecond}", plan.RequestsPerSecond, "tenant", plan.TenantId.ToString(), plan.Plan);
        }

        var client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return new Partition($"client:{client}:{options.PerClientRequestsPerSecond}", options.PerClientRequestsPerSecond, "client", Tenant: null, Plan: null);
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
