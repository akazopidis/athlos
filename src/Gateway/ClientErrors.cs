using System.Net.Http.Json;
using System.Threading.RateLimiting;

namespace Athlos.Gateway;

// Page crashes in the SPA (design §6): the error boundary posts what broke to POST /client-errors,
// and the gateway logs it, so it reaches Application Insights with the gateway's other telemetry
// (release, time) instead of staying in one visitor's console. It keeps no visitor data: no cookies
// or tokens are read, and the page's address is kept without its query string. Bodies over 16 KB are
// refused, long fields are cut, and each client may report 10 errors a minute.
public static partial class ClientErrors
{
    private const long MaxBodyBytes = 16 * 1024;
    private const int MaxField = 2_000;

    // One per gateway instance (a singleton), like the gateway's other buckets.
    public sealed class Limiter : IDisposable
    {
        public PartitionedRateLimiter<string> PerClient { get; } = PartitionedRateLimiter.Create<string, string>(client =>
            RateLimitPartition.GetFixedWindowLimiter(client, _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));

        public void Dispose() => PerClient.Dispose();
    }

    public sealed record Report(string? Message, string? Stack, string? Path);

    public static void MapClientErrors(this IEndpointRouteBuilder app) =>
        app.MapPost("/client-errors", HandleAsync).ExcludeFromDescription();

    private static async Task<IResult> HandleAsync(HttpContext context, Limiter limiter, ILoggerFactory loggers)
    {
        using var lease = limiter.PerClient.AttemptAcquire(context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        if (!lease.IsAcquired)
        {
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        if (context.Request.ContentLength is not { } length || length > MaxBodyBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        Report? report;
        try
        {
            report = await context.Request.ReadFromJsonAsync<Report>(context.RequestAborted);
        }
        catch (System.Text.Json.JsonException)
        {
            return Results.BadRequest();
        }

        if (string.IsNullOrWhiteSpace(report?.Message))
        {
            return Results.BadRequest();
        }

        var path = report.Path is { } p ? p.Split('?', '#')[0] : null;
        LogPageCrashed(loggers.CreateLogger("Athlos.Gateway.ClientErrors"), Cut(report.Message), Cut(path), Cut(report.Stack));
        return Results.NoContent();
    }

    private static string? Cut(string? value) => value is { Length: > MaxField } ? value[..MaxField] : value;

    [LoggerMessage(Level = LogLevel.Error, Message = "A page crashed in the browser: {Error} on {Page}\n{Stack}")]
    private static partial void LogPageCrashed(ILogger logger, string? error, string? page, string? stack);
}
